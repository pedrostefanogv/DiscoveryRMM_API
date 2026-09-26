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

            // Dedupe: respostas repetidas entre chamados custam uma única chamada.
            var textsByAnswer = candidates.ToDictionary(
                item => item.AnswerId,
                item => BuildText(item.QuestionLabel, item.ValueText));
            var distinctInputs = textsByAnswer.Values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var indexByText = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < distinctInputs.Count; i++)
                indexByText[distinctInputs[i]] = i;

            var first = candidates[0];
            var credential = await credentialResolver.ResolveAsync(first.ClientId, first.SiteId, ct);
            var baseUrl = credential?.EffectiveEmbeddingBaseUrl
                ?? (string.IsNullOrWhiteSpace(settings.EmbeddingBaseUrl) ? settings.BaseUrl : settings.EmbeddingBaseUrl);
            var apiKey = credential?.EffectiveEmbeddingApiKey
                ?? (string.IsNullOrWhiteSpace(settings.EmbeddingApiKey) ? settings.ApiKey : settings.EmbeddingApiKey);

            var embeddings = await embeddingProvider.GenerateEmbeddingsAsync(
                distinctInputs, settings.EmbeddingModel, apiKey, baseUrl, ct);
            if (embeddings.Count == 0) break;

            // Divergência de dimensão: NÃO grava — o AutoSync/reset do ciclo
            // realinha a coluna e o próximo ciclo reprocessa.
            if (embeddings[0].Length != settings.EmbeddingDimensions)
            {
                logger.LogWarning(
                    "[TicketAnswerEmbedding] Dimensão {Actual} difere da configurada {Expected}. Aguardando realinhamento.",
                    embeddings[0].Length, settings.EmbeddingDimensions);
                break;
            }

            var updates = new List<TicketAnswerEmbeddingUpdate>(candidates.Count);
            foreach (var candidate in candidates)
            {
                if (!textsByAnswer.TryGetValue(candidate.AnswerId, out var text)) continue;
                if (!indexByText.TryGetValue(text, out var index)) continue;
                if (index >= embeddings.Count) continue;
                updates.Add(new TicketAnswerEmbeddingUpdate(candidate.AnswerId, embeddings[index]));
            }

            await repository.UpdateEmbeddingsAsync(updates, ct);
            processed += updates.Count;

            logger.LogDebug(
                "[TicketAnswerEmbedding] Lote processado: {Count} respostas ({Distinct} textos distintos).",
                updates.Count, distinctInputs.Count);
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
