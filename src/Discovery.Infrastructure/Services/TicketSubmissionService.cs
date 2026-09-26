using System.Text;
using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <inheritdoc />
public class TicketSubmissionService(
    DiscoveryDbContext db,
    IDepartmentCustomFieldService departmentCustomFieldService,
    ILogger<TicketSubmissionService> logger) : ITicketSubmissionService
{
    public async Task<TicketSubmissionResult> PrepareAsync(
        TicketSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        TicketTemplate? template = null;
        if (request.TemplateId.HasValue)
        {
            template = await db.TicketTemplates
                .AsNoTracking()
                // Template excluído (lixeira) não pode pré-preencher chamados.
                .FirstOrDefaultAsync(t => t.Id == request.TemplateId.Value && t.DeletedAt == null && t.IsActive, cancellationToken);

            // TemplateId informado e inexistente/inativo NÃO é silencioso.
            if (template is null)
            {
                return new TicketSubmissionResult(
                    request.DepartmentId,
                    request.Title ?? string.Empty,
                    request.Description ?? string.Empty,
                    request.Category,
                    request.Priority,
                    new Dictionary<Guid, string>(),
                    new[] { new DepartmentFieldValidationError(
                        request.TemplateId.Value, "TemplateId", "Template de chamado não encontrado ou inativo.") },
                    null);
            }
        }

        var title = string.IsNullOrWhiteSpace(request.Title) ? template?.Title ?? string.Empty : request.Title!.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description)
            ? template?.Description ?? string.Empty
            : request.Description!;
        var category = string.IsNullOrWhiteSpace(request.Category) ? template?.Category : request.Category!.Trim();
        var priority = string.IsNullOrWhiteSpace(request.Priority)
            ? template?.Priority?.ToString()
            : request.Priority!.Trim();

        var departmentId = request.DepartmentId ?? template?.DepartmentId;
        var errors = new List<DepartmentFieldValidationError>();

        // ── Escopo do template ────────────────────────────────────────────────
        // Existir não basta: o template precisa ser aplicável a este cliente e
        // a este departamento (a UI filtra, a API confere).
        if (template is not null)
        {
            if (template.ClientId.HasValue && template.ClientId.Value != request.ClientId)
            {
                errors.Add(new DepartmentFieldValidationError(
                    template.Id, "TemplateId", "Template não pertence a este cliente."));
            }

            if (template.DepartmentId.HasValue && request.DepartmentId.HasValue
                && template.DepartmentId.Value != request.DepartmentId.Value)
            {
                errors.Add(new DepartmentFieldValidationError(
                    template.Id, "TemplateId", "Template não pertence a este departamento."));
            }
        }

        // ── Departamento obrigatório ──────────────────────────────────────────
        // O departamento define responsável (auto-atribuição) e perfil/SLA.
        if (departmentId is null)
        {
            errors.Add(new DepartmentFieldValidationError(
                Guid.Empty, "DepartmentId", "Selecione um departamento para abrir o chamado."));
        }
        else
        {
            // Escopo: departamento do próprio cliente ou global. Um id de outro
            // cliente (uso indevido da API) não pode receber o chamado. Quando a
            // linha não existe, a validação é da FK na gravação.
            var department = await db.Departments
                .AsNoTracking()
                .Where(d => d.Id == departmentId.Value)
                .Select(d => new { d.ClientId, d.IsActive })
                .FirstOrDefaultAsync(cancellationToken);

            if (department is null)
            {
                logger.LogWarning(
                    "Departamento {DepartmentId} não encontrado ao preparar o chamado do cliente {ClientId}.",
                    departmentId.Value, request.ClientId);
            }
            else
            {
                if (department.ClientId.HasValue && department.ClientId.Value != request.ClientId)
                {
                    errors.Add(new DepartmentFieldValidationError(
                        departmentId.Value, "DepartmentId", "Departamento não pertence a este cliente."));
                }

                if (!department.IsActive)
                {
                    errors.Add(new DepartmentFieldValidationError(
                        departmentId.Value, "DepartmentId", "Departamento inativo."));
                }
            }
        }

        // ── Mini questionário do template (não são campos do chamado) ────────
        var questions = TicketTemplateQuestions.Parse(template?.QuestionsJson);
        if (request.TemplateAnswers is { Count: > 0 } && template is null)
        {
            errors.Add(new DepartmentFieldValidationError(
                Guid.Empty, "TemplateAnswers", "Respostas de questionário exigem um template selecionado."));
        }
        foreach (var answerError in TicketTemplateQuestions.ValidateAnswers(questions, request.TemplateAnswers))
        {
            errors.Add(new DepartmentFieldValidationError(Guid.Empty, answerError.Key, answerError.Message));
        }

        // ── Campos do departamento: SEMPRE valem para todo chamado do
        // departamento, independentes do template (obrigatório ou não conforme
        // a configuração de cada campo). ─────────────────────────────────────
        var merged = new Dictionary<Guid, JsonElement>();
        // Ids vindos do default do template: um default órfão (campo removido do
        // departamento) é ignorado, enquanto um valor enviado pelo cliente para
        // definição inexistente continua sendo erro.
        var templateDefaultIds = new HashSet<Guid>();
        foreach (var (definitionId, value) in ParseTemplateDefaults(template?.CustomFieldDefaultsJson))
        {
            merged[definitionId] = value;
            templateDefaultIds.Add(definitionId);
        }
        if (request.CustomFieldValues is not null)
            foreach (var (definitionId, value) in request.CustomFieldValues)
                merged[definitionId] = value;

        if (departmentId is null)
        {
            // O erro de departamento obrigatório já foi registrado acima; aqui só
            // garantimos que nenhum valor seja aplicado sem departamento.
            merged.Clear();
        }

        var rawValues = new Dictionary<Guid, string>();
        var definitions = new Dictionary<Guid, CustomFieldDefinition>();

        if (departmentId.HasValue)
        {
            definitions = await db.CustomFieldDefinitions
                .AsNoTracking()
                .Where(d => d.DepartmentId == departmentId.Value && d.IsActive && !d.IsInternal)
                .ToDictionaryAsync(d => d.Id, cancellationToken);

            foreach (var (definitionId, value) in merged)
            {
                if (!definitions.ContainsKey(definitionId))
                {
                    var fromRequest = request.CustomFieldValues?.ContainsKey(definitionId) == true;
                    if (!fromRequest && templateDefaultIds.Contains(definitionId))
                    {
                        // Default antigo de campo que não existe mais no departamento:
                        // não pode impedir a abertura do chamado.
                        logger.LogWarning(
                            "Template default ignorado: definição {DefinitionId} não pertence ao departamento {DepartmentId}.",
                            definitionId, departmentId.Value);
                        continue;
                    }

                    errors.Add(new DepartmentFieldValidationError(
                        definitionId, definitionId.ToString("D"),
                        "O campo personalizado informado não pertence ao departamento."));
                    continue;
                }
                rawValues[definitionId] = value.GetRawText();
            }

            // Valida TODOS os campos do departamento (inclusive os obrigatórios
            // que não vieram preenchidos).
            if (errors.Count == 0)
            {
                errors.AddRange(await departmentCustomFieldService.ValidateTicketFieldsAsync(
                    departmentId.Value, rawValues, cancellationToken));
            }
        }

        if (errors.Count > 0)
        {
            return new TicketSubmissionResult(
                departmentId, title, description, category, priority, rawValues, errors, null);
        }

        var needsSnapshot = template is not null
            || rawValues.Count > 0
            || request.TemplateAnswers is { Count: > 0 };

        var snapshot = needsSnapshot
            ? await BuildSnapshot(template, questions, request.TemplateAnswers, departmentId, definitions, rawValues, priority, category, title)
            : null;

        var answerDrafts = BuildAnswerDrafts(questions, request.TemplateAnswers);

        return new TicketSubmissionResult(
            departmentId, title, description, category, priority, rawValues, errors, snapshot,
            template?.Id, answerDrafts, TemplateDisplayName(template));
    }

    /// <inheritdoc />
    public async Task SaveTemplateAnswersAsync(
        Guid ticketId,
        Guid? templateId,
        IReadOnlyList<TicketAnswerDraft> answers,
        CancellationToken cancellationToken = default)
    {
        if (answers.Count == 0) return;

        // Reexecução (replay/idempotência) não deve violar o índice único
        // (ticket_id, question_key): substitui as respostas do chamado.
        // SaveChanges separado garante que o DELETE seja aplicado ANTES dos
        // INSERTs (mesma tabela, índice único).
        var existing = await db.TicketAnswers
            .Where(a => a.TicketId == ticketId)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            db.TicketAnswers.RemoveRange(existing);
            await db.SaveChangesAsync(cancellationToken);
        }

        var now = DateTime.UtcNow;
        for (var index = 0; index < answers.Count; index++)
        {
            var answer = answers[index];
            db.TicketAnswers.Add(new TicketAnswer
            {
                Id = Guid.NewGuid(),
                TicketId = ticketId,
                TemplateId = templateId,
                QuestionKey = answer.QuestionKey,
                QuestionLabel = answer.QuestionLabel,
                ValueText = answer.ValueText,
                ValueJson = answer.ValueJson,
                IsSensitive = answer.IsSensitive,
                SortOrder = index,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<TicketAnswerDraft> BuildAnswerDrafts(
        IReadOnlyList<TicketTemplateQuestion> questions,
        IReadOnlyDictionary<string, JsonElement>? answers)
    {
        if (answers is null || answers.Count == 0 || questions.Count == 0)
            return Array.Empty<TicketAnswerDraft>();

        var drafts = new List<TicketAnswerDraft>();
        foreach (var question in questions)
        {
            if (!answers.TryGetValue(question.Key, out var value)) continue;
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;

            var text = TicketTemplateQuestions.FormatAnswer(question, value);
            if (string.IsNullOrWhiteSpace(text)) continue;

            drafts.Add(new TicketAnswerDraft(question.Key, question.Label, text, value.GetRawText(), question.IsSensitive));
        }

        return drafts;
    }

    /// <summary>
    /// Nome exibido do template (histórico do chamado e snapshot). A chave
    /// (Name) é identificador padronizado, então o texto humano é o Title.
    /// </summary>
    private static string TemplateDisplayName(TicketTemplate? template)
        => template is null
            ? string.Empty
            : string.IsNullOrWhiteSpace(template.Title) ? template.Name : template.Title;

    private static IEnumerable<(Guid DefinitionId, JsonElement Value)> ParseTemplateDefaults(string? defaultsJson)
    {
        if (string.IsNullOrWhiteSpace(defaultsJson)) yield break;

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(defaultsJson);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) yield break;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (Guid.TryParse(property.Name, out var definitionId))
                    yield return (definitionId, property.Value.Clone());
            }
        }
    }

    private async Task<string?> BuildSnapshot(
        TicketTemplate? template,
        IReadOnlyList<TicketTemplateQuestion> questions,
        IReadOnlyDictionary<string, JsonElement>? answers,
        Guid? departmentId,
        IReadOnlyDictionary<Guid, CustomFieldDefinition> definitions,
        IReadOnlyDictionary<Guid, string> rawValues,
        string? priority,
        string? category,
        string title)
    {
        var builder = new StringBuilder();
        builder.AppendLine("### Formulário do chamado");
        builder.AppendLine();

        if (template is not null)
            builder.AppendLine($"- **Template:** {Escape(TemplateDisplayName(template))}");
        if (!string.IsNullOrWhiteSpace(title))
            builder.AppendLine($"- **Título enviado:** {Escape(title)}");
        if (!string.IsNullOrWhiteSpace(priority))
            builder.AppendLine($"- **Prioridade:** {Escape(priority)}");
        if (!string.IsNullOrWhiteSpace(category))
            builder.AppendLine($"- **Categoria:** {Escape(category)}");

        if (departmentId.HasValue)
        {
            var departmentName = await db.Departments
                .AsNoTracking()
                .Where(d => d.Id == departmentId.Value)
                .Select(d => d.Name)
                .FirstOrDefaultAsync();
            if (!string.IsNullOrWhiteSpace(departmentName))
                builder.AppendLine($"- **Departamento:** {Escape(departmentName)}");
        }

        builder.AppendLine($"- **Enviado em:** {DateTime.UtcNow:dd/MM/yyyy HH:mm} (UTC)");

        // ── Questionário do modelo ───────────────────────────────────────────
        if (questions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("#### Questionário do modelo");
            builder.AppendLine();
            builder.AppendLine("| Pergunta | Resposta |");
            builder.AppendLine("| --- | --- |");
            foreach (var question in questions)
            {
                var answer = answers is not null && answers.TryGetValue(question.Key, out var value)
                    ? TicketTemplateQuestions.FormatAnswer(question, value)
                    : string.Empty;
                builder.AppendLine($"| {Escape(question.Label)} | {Escape(string.IsNullOrWhiteSpace(answer) ? "—" : answer)} |");
            }
        }

        // ── Campos do departamento (pré-preenchidos ou informados) ───────────
        var rows = new List<(string Label, string Value)>();
        foreach (var (definitionId, rawValue) in rawValues)
        {
            if (!definitions.TryGetValue(definitionId, out var definition)) continue;
            if (definition.IsSecret) continue;

            var formatted = FormatValue(rawValue);
            if (string.IsNullOrWhiteSpace(formatted)) continue;
            rows.Add((definition.Label, formatted));
        }

        if (rows.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("#### Campos do departamento");
            builder.AppendLine();
            builder.AppendLine("| Campo | Valor |");
            builder.AppendLine("| --- | --- |");
            foreach (var (label, value) in rows)
                builder.AppendLine($"| {Escape(label)} | {Escape(value)} |");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatValue(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var value = document.RootElement;
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return string.Empty;
                case JsonValueKind.True:
                    return "Sim";
                case JsonValueKind.False:
                    return "Não";
                case JsonValueKind.Array:
                    return string.Join(", ", value.EnumerateArray().Select(item => item.ToString()));
                case JsonValueKind.String:
                    return value.GetString() ?? string.Empty;
                default:
                    return value.ToString();
            }
        }
        catch (JsonException)
        {
            return rawJson;
        }
    }

    private static string Escape(string value) =>
        value.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
}
