namespace Discovery.Core.Interfaces;

/// <summary>Resolve o responsável de um chamado por estratégia do departamento (round-robin/least-open).</summary>
public interface ITicketAssignmentService
{
    Task<Guid?> ResolveAssigneeAsync(Guid departmentId, CancellationToken ct = default);
}
