using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Discovery.Infrastructure.Services;

/// <inheritdoc />
/// <remarks>
/// O agent NÃO tem lista de campos/opções embutida no frontend: o formulário de
/// abertura é montado a partir do que este serviço devolve. As opções de
/// categoria variam por departamento (modelos ativos do departamento e
/// categorias já usadas em chamados dele) e ainda aceitam o catálogo base
/// configurado no servidor (Tickets:CategoryOptions, lista simples ou itens
/// {value,label}, editável sem recompilar o agent). Quando o departamento não
/// tem nenhuma categoria, o campo é devolvido com Visible=false — e o agent não
/// o exibe.
/// </remarks>
public sealed class TicketFormSchemaService(
    DiscoveryDbContext db,
    IConfiguration configuration) : ITicketFormSchemaService
{
    /// <summary>Seção de configuração com o catálogo base de categorias do servidor.</summary>
    public const string CategoryOptionsSection = "Tickets:CategoryOptions";

    /// <summary>Chave do campo de categoria no schema.</summary>
    public const string CategoryFieldKey = "category";

    /// <summary>Chave do campo de prioridade no schema.</summary>
    public const string PriorityFieldKey = "priority";

    /// <summary>Prioridade padrão do servidor (Medium no enum do ticket = 2 no contrato do agent).</summary>
    public const string DefaultPriorityValue = "2";

    /// <summary>Limite de categorias recentes lidas do histórico do departamento.</summary>
    private const int UsedCategoriesScanLimit = 200;

    /// <summary>Limite final de categorias usadas no schema (o catálogo base sempre entra).</summary>
    private const int UsedCategoriesLimit = 50;

    public async Task<TicketFormSchemaDto> GetForDepartmentAsync(
        Guid departmentId,
        Guid? clientId,
        CancellationToken cancellationToken = default)
    {
        var categories = await BuildCategoryOptionsAsync(departmentId, clientId, cancellationToken);

        var fields = new List<TicketFormFieldDto>(2)
        {
            // Categoria é texto livre no ticket (até 100 chars no servidor), mas
            // o picker do agent lista só o que o servidor conhece. Sem nenhuma
            // categoria conhecida o campo NÃO é exibido.
            new TicketFormFieldDto(
                CategoryFieldKey,
                "Categoria",
                "select",
                Required: false,
                Visible: categories.Count > 0,
                DefaultValue: null,
                Options: categories),
            new TicketFormFieldDto(
                PriorityFieldKey,
                "Prioridade",
                "select",
                Required: false,
                Visible: true,
                DefaultValue: DefaultPriorityValue,
                Options: PriorityOptions()),
        };

        return new TicketFormSchemaDto(departmentId.ToString(), fields);
    }

    /// <summary>
    /// Categorias conhecidas do departamento, sem duplicatas de VALOR
    /// (comparação case-insensitive) e na ordem: modelos do departamento,
    /// chamados do departamento (mais recentes primeiro) e catálogo base do
    /// servidor. O catálogo base pode definir o rótulo exibido sem mexer no
    /// valor gravado no chamado (ver `Tickets:CategoryOptions`).
    /// </summary>
    private async Task<IReadOnlyList<TicketFormFieldOptionDto>> BuildCategoryOptionsAsync(
        Guid departmentId,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        var options = new List<TicketFormFieldOptionDto>();
        var indexByValue = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value, string? label = null)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return;
            var cleanLabel = label?.Trim();
            if (indexByValue.TryGetValue(trimmed, out var existing))
            {
                // Rótulo explícito do catálogo base "humaniza" uma categoria que
                // já veio de template/histórico; o valor continua idêntico ao
                // que já está gravado nos chamados.
                if (!string.IsNullOrEmpty(cleanLabel) && options[existing].Label == options[existing].Value)
                    options[existing] = new TicketFormFieldOptionDto(options[existing].Value, cleanLabel);
                return;
            }
            indexByValue[trimmed] = options.Count;
            options.Add(new TicketFormFieldOptionDto(
                trimmed,
                string.IsNullOrEmpty(cleanLabel) ? trimmed : cleanLabel));
        }

        // 1) Categorias dos modelos ativos do departamento (globais + do
        // cliente). Modelo SEM departamento é oferecido para qualquer
        // departamento pelo picker do agent, então a categoria dele também vale
        // aqui — sem isso o modelo apareceria no formulário com uma categoria
        // fora da lista.
        var templateCategories = await db.TicketTemplates
            .AsNoTracking()
            .Where(t => t.DeletedAt == null
                && t.IsActive
                && (t.DepartmentId == departmentId || t.DepartmentId == null)
                && (clientId == null || t.ClientId == null || t.ClientId == clientId))
            .OrderBy(t => t.Title)
            .ThenBy(t => t.Name)
            .Select(t => t.Category)
            .ToListAsync(cancellationToken);

        foreach (var category in templateCategories)
            Add(category);

        // 2) Categorias já usadas em chamados do departamento (mais recentes
        // primeiro): histórico real do servidor, não uma lista inventada.
        if (clientId.HasValue)
        {
            var used = await db.Tickets
                .AsNoTracking()
                .Where(t => t.ClientId == clientId.Value
                    && t.DepartmentId == departmentId
                    && t.Category != null
                    && t.Category != "")
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => t.Category!)
                .Take(UsedCategoriesScanLimit)
                .ToListAsync(cancellationToken);

            var added = 0;
            foreach (var category in used)
            {
                var before = options.Count;
                Add(category);
                if (options.Count > before && ++added >= UsedCategoriesLimit)
                    break;
            }
        }

        // 3) Catálogo base configurado no servidor (appsettings/env:
        // Tickets:CategoryOptions). Aceita lista simples ("Hardware") ou itens
        // com rótulo próprio ({"value":"Acesso","label":"Acesso / Permissões"}):
        // o VALOR é o que vai gravado no chamado, o rótulo é só apresentação.
        // Fica por último para não ofuscar as categorias do departamento.
        foreach (var entry in configuration.GetSection(CategoryOptionsSection).GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(entry.Value))
            {
                Add(entry.Value);
                continue;
            }
            Add(entry["value"], entry["label"]);
        }

        return options;
    }

    /// <summary>
    /// Prioridades do contrato do agent (1=Baixa, 2=Média, 3=Alta, 4=Crítica),
    /// as mesmas aceitas no POST de abertura. O valor padrão é Medium.
    /// </summary>
    private static IReadOnlyList<TicketFormFieldOptionDto> PriorityOptions() =>
    [
        new("1", "Baixa"),
        new("2", "Média"),
        new("3", "Alta"),
        new("4", "Crítica"),
    ];
}
