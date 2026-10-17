using Discovery.Core.Cqrs;
using Discovery.Core.Entities;

namespace Discovery.Core.Departments;

/// <summary>
/// Validador ÚNICO do vínculo de herança de departamento (create e update).
///
/// Regras (D1–D4):
/// - não permite auto-herança (InheritFromGlobalId == própria id);
/// - o pai precisa existir, estar ativo e ser GLOBAL (ClientId == null);
/// - cross-tenant é rejeitado porque só departamento global pode ser pai;
/// - ciclo é detectado caminhando a cadeia de vínculos com limite de
///   <see cref="MaxDepth"/> saltos (dados legados podem ter ciclos).
///
/// A classe é pura: recebe apenas os dados e uma função de busca, sem EF/DbContext,
/// para poder ser usada pelos dois handlers e testada isoladamente.
/// </summary>
public static class DepartmentInheritanceValidator
{
    /// <summary>Limite defensivo da caminhada de ciclo (dados legados).</summary>
    public const int MaxDepth = 16;

    /// <summary>Aviso: o vínculo aponta para um pai inativo (ignorado na leitura).</summary>
    public const string InactiveParentWarning = "departamento pai inativo foi ignorado";

    /// <summary>Aviso: vínculo legado aponta para departamento de cliente (não global).</summary>
    public const string NonGlobalParentWarning = "vínculo legado para departamento não global foi ignorado";

    /// <summary>Aviso: a cadeia de vínculos forma um ciclo (dados legados).</summary>
    public const string CycleWarning = "ciclo detectado nos dados legados";

    /// <summary>Aviso: o vínculo aponta para um departamento que não existe mais.</summary>
    public const string MissingParentWarning = "departamento pai não encontrado foi ignorado";

    /// <summary>
    /// Diagnóstico de leitura (não rejeita): devolve o pai utilizável e os
    /// avisos de herança para o console. Diferente de <see cref="ValidateAsync"/>,
    /// que bloqueia o vínculo inválido no create/update.
    /// </summary>
    public sealed record Diagnosis(Department? UsableParent, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Inspeciona o vínculo atual de um departamento sem alterar nada.
    /// O pai só é devolvido quando é GLOBAL, ATIVO e a cadeia não tem ciclo;
    /// nos demais casos o vínculo é ignorado e um aviso é emitido.
    /// </summary>
    public static async Task<Diagnosis> DiagnoseAsync(
        Department department,
        Func<Guid, CancellationToken, Task<Department?>> findAsync,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();

        // Departamento global nunca herda; vínculo legado é ignorado.
        if (department.ClientId is null)
            return new Diagnosis(null, warnings);

        if (department.InheritFromGlobalId is not { } parentId)
            return new Diagnosis(null, warnings);

        if (parentId == department.Id)
        {
            warnings.Add(CycleWarning);
            return new Diagnosis(null, warnings);
        }

        var parent = await findAsync(parentId, ct);
        if (parent is null)
        {
            warnings.Add(MissingParentWarning);
            return new Diagnosis(null, warnings);
        }

        // Contrato de um salto: só departamento GLOBAL pode ser pai.
        if (parent.ClientId is not null)
        {
            warnings.Add(NonGlobalParentWarning);
            return new Diagnosis(null, warnings);
        }

        if (!parent.IsActive)
        {
            warnings.Add(InactiveParentWarning);
            return new Diagnosis(null, warnings);
        }

        // Caminhada defensiva da cadeia: dados legados podem ter ciclo mesmo
        // com o contrato de um salto. Ciclo invalida o vínculo.
        var visited = new HashSet<Guid> { department.Id, parent.Id };
        var current = parent;
        for (var depth = 1; depth <= MaxDepth; depth++)
        {
            ct.ThrowIfCancellationRequested();

            var nextId = current.InheritFromGlobalId;
            if (nextId is null)
                return new Diagnosis(parent, warnings);

            if (!visited.Add(nextId.Value))
            {
                warnings.Add(CycleWarning);
                return new Diagnosis(null, warnings);
            }

            var next = await findAsync(nextId.Value, ct);
            if (next is null)
                return new Diagnosis(parent, warnings); // vínculo órfão adiante: não afeta o pai direto

            current = next;
        }

        warnings.Add(CycleWarning);
        return new Diagnosis(null, warnings);
    }

    /// <summary>
    /// Valida o vínculo. <paramref name="inheritFromGlobalId"/> nulo significa
    /// "desfazer a herança" e é sempre válido (D5).
    /// </summary>
    /// <param name="departmentId">Id do departamento sendo criado/atualizado (nulo no create antes de gerar o id).</param>
    /// <param name="inheritFromGlobalId">Novo vínculo; nulo = sem herança.</param>
    /// <param name="findAsync">Busca um departamento por id (inclui inativos).</param>
    public static async Task<Error?> ValidateAsync(
        Guid? departmentId,
        Guid? inheritFromGlobalId,
        Func<Guid, CancellationToken, Task<Department?>> findAsync,
        CancellationToken ct = default)
    {
        if (inheritFromGlobalId is null)
            return null;

        var parentId = inheritFromGlobalId.Value;

        if (departmentId.HasValue && parentId == departmentId.Value)
            return Error.Validation(
                "InheritFromGlobalId",
                "Um departamento não pode herdar de si mesmo.");

        var parent = await findAsync(parentId, ct);
        if (parent is null)
            return Error.Validation(
                "InheritFromGlobalId",
                "Departamento global de origem não encontrado.");

        if (!parent.IsActive)
            return Error.Validation(
                "InheritFromGlobalId",
                "O departamento global de origem está inativo e não pode ser herdado.");

        if (parent.ClientId is not null)
            return Error.Validation(
                "InheritFromGlobalId",
                "Somente departamentos globais (sem cliente) podem ser herdados; o pai informado é de um cliente.");

        // Caminhada defensiva: dados legados podem ter cadeia/ciclo. O contrato é
        // de um salto, mas percorremos a cadeia com cap para recusar vínculos que
        // voltem ao próprio departamento (ex.: A -> B -> A).
        var visited = new HashSet<Guid> { parentId };
        if (departmentId.HasValue)
            visited.Add(departmentId.Value);

        var current = parent;
        for (var depth = 1; depth <= MaxDepth; depth++)
        {
            ct.ThrowIfCancellationRequested();

            var nextId = current.InheritFromGlobalId;
            if (nextId is null)
                return null;

            if (!visited.Add(nextId.Value))
                return Error.Validation(
                    "InheritFromGlobalId",
                    "Ciclo de herança detectado: o departamento de origem aponta de volta para a cadeia informada.");

            var next = await findAsync(nextId.Value, ct);
            if (next is null)
                return null; // vínculo legado órfão: a leitura defensiva o ignora

            current = next;
        }

        return Error.Validation(
            "InheritFromGlobalId",
            $"A cadeia de herança excede o limite de {MaxDepth} níveis.");
    }
}
