using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Departments.Commands;

namespace Discovery.Core.Cqrs.Departments.Queries;

/// <summary>
/// Listagem de departamentos.
/// - com <see cref="ClientId"/>: departamentos do cliente (+ globais se
///   <see cref="IncludeGlobal"/>) — comportamento histórico;
/// - sem <see cref="ClientId"/> e com <see cref="AllClients"/> = true: TODOS os
///   departamentos (globais + de todos os clientes), usado pelo filtro de escopo
///   "todos" do console (D11);
/// - sem <see cref="ClientId"/> e sem <see cref="AllClients"/>: apenas os
///   globais — comportamento histórico preservado para os demais chamadores.
/// </summary>
public sealed record ListDepartmentsQuery(
    Guid? ClientId, bool IncludeGlobal = true, bool ActiveOnly = false,
    bool AllClients = false)
    : IQuery<Result<IReadOnlyList<DepartmentDto>>>;

public sealed record GetDepartmentByIdQuery(Guid Id)
    : IQuery<Result<DepartmentDto>>;
