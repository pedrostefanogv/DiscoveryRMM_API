using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Gera embeddings das respostas do questionário (backfill + novas respostas).
/// Roda DENTRO do ciclo do <c>KnowledgeEmbeddingJob</c> para compartilhar o
/// AutoSync de dimensão, o circuit breaker do reset e a resolução de credenciais
/// por escopo — evitando um segundo agendamento e duas fontes de verdade.
///
/// Otimizações: lote fixo, dedupe de textos idênticos (uma chamada por texto),
/// marca como processado o que não agrega (curto/booleano) para não voltar à
/// fila, e teto por ciclo para não sobrecarregar o provedor.
/// </summary>
public class TicketAnswerEmbeddingProcessor(
    ITicketAnswerRepository repository,
    IEmbeddingProvider embeddingProvider,
    IAiCredentialResolver credentialResolver,
    ILogger<TicketAnswerEmbeddingProcessor> logger) : ITicketAnswerEmbeddingProcessor
{
    /// <summary>Teto de respostas processadas por ciclo (30s).</summary>
    public const int MaxAnswersPerCycle = 50;

    /// <summary>Tamanho do lote (uma chamada de embeddings por lote).</summary>
    public const int BatchSize = 25;

    /// <summary>Textos abaixo disso (ex: "Sim"/"Não") não agregam busca semântica.</summary>
    private const int MinEmbeddableLength = 15;

    private const int MaxTextLength = 2000;

    public async Task<int> ProcessAsync(AIIntegrationSettings settings, CancellationToken ct = default)
    {
        var processed = 0;
        var maxBatches = Math.Max(1, MaxAnswersPerCycle / BatchSize);

        for (var batch = 0; batch < maxBatches; batch++)
        {
            var pending = await repository.GetWithoutEmbeddingAsync(BatchSize, ct);
            if (pending.Count == 0) break;

            // Pula o que não agrega e MARCA como processado (senão voltaria à
            // fila a cada ciclo).
            var skippable = pending.Where(item => !IsEmbeddable(item.ValueText))
                .Select(item => item.AnswerId)
                .ToList();
            if (skippable.Count > 0)
            {
                await repository.MarkProcessedWithoutEmbeddingAsync(skippable, ct);
                logger.LogDebug("[TicketAnswerEmbedding] {Count} resposta(s) puladas (curtas/booleanas).", skippable.Count);
            }

            var candidates = pending.Where(item => IsEmbeddable(item.ValueText)).ToList();
            if (candidates.Count == 0) continue;

            var groupedByScope = candidates.GroupBy(c => (c.ClientId, c.SiteId));
            foreach (var scopeGroup in groupedByScope)
            {
                var scopeCandidates = scopeGroup.ToList();

                // Dedupe dentro do escopo: respostas repetidas entre chamados do mesmo tenant custam uma única chamada.
                var textsByAnswer = scopeCandidates.ToDictionary(
                    item => item.AnswerId,
                    item => BuildText(item.QuestionLabel, item.ValueText));
                var distinctInputs = textsByAnswer.Values
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var indexByText = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < distinctInputs.Count; i++)
                    indexByText[distinctInputs[i]] = i;

                var credential = await credentialResolver.ResolveAsync(scopeGroup.Key.ClientId, scopeGroup.Key.SiteId, ct);
                var baseUrl = credential?.EffectiveEmbeddingBaseUrl
                    ?? (string.IsNullOrWhiteSpace(settings.EmbeddingBaseUrl) ? settings.BaseUrl : settings.EmbeddingBaseUrl);
                var apiKey = credential?.EffectiveEmbeddingApiKey
                    ?? (string.IsNullOrWhiteSpace(settings.EmbeddingApiKey) ? settings.ApiKey : settings.EmbeddingApiKey);

                var embeddings = await embeddingProvider.GenerateEmbeddingsAsync(
                    distinctInputs, settings.EmbeddingModel, apiKey, baseUrl, ct);
                if (embeddings.Count == 0) continue;

                // Divergência de dimensão: NÃO grava — o AutoSync/reset do ciclo
                // realinha a coluna e o próximo ciclo reprocessa.
                if (embeddings[0].Length != settings.EmbeddingDimensions)
                {
                    logger.LogWarning(
                        "[TicketAnswerEmbedding] Dimensão {Actual} difere da configurada {Expected}. Aguardando realinhamento.",
                        embeddings[0].Length, settings.EmbeddingDimensions);
                    return processed;
                }

                var updates = new List<TicketAnswerEmbeddingUpdate>(scopeCandidates.Count);
                foreach (var candidate in scopeCandidates)
                {
                    if (!textsByAnswer.TryGetValue(candidate.AnswerId, out var text)) continue;
                    if (!indexByText.TryGetValue(text, out var index)) continue;
                    if (index >= embeddings.Count) continue;
                    updates.Add(new TicketAnswerEmbeddingUpdate(candidate.AnswerId, embeddings[index]));
                }

                await repository.UpdateEmbeddingsAsync(updates, ct);
                processed += updates.Count;

                logger.LogDebug(
                    "[TicketAnswerEmbedding] Lote processado (ClientId={ClientId}): {Count} respostas ({Distinct} textos distintos).",
                    scopeGroup.Key.ClientId, updates.Count, distinctInputs.Count);
            }
        }

        return processed;
    }

    private static bool IsEmbeddable(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Trim().Length >= MinEmbeddableLength;

    private static string BuildText(string label, string value)
    {
        var text = string.IsNullOrWhiteSpace(label)
            ? value.Trim()
            : $"{label.Trim()}: {value.Trim()}";
        return text.Length <= MaxTextLength ? text : text[..MaxTextLength];
    }
}
