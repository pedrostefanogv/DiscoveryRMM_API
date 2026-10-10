using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Monta o schema do formulário de abertura de chamado do agent para um
/// departamento. O agent não tem lista fixa de campos nem de opções: ele
/// renderiza exatamente o que este serviço devolve (chave, rótulo,
/// obrigatoriedade, visibilidade, valor padrão e opções).
/// </summary>
public interface ITicketFormSchemaService
{
    /// <param name="departmentId">Departamento escolhido pelo usuário; as opções variam por ele.</param>
    /// <param name="clientId">Cliente do site do agent (null quando o site não resolve o cliente).</param>
    Task<TicketFormSchemaDto> GetForDepartmentAsync(
        Guid departmentId,
        Guid? clientId,
        CancellationToken cancellationToken = default);
}
