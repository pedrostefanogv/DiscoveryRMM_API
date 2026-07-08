using MediatR;

namespace Discovery.Core.Cqrs;

/// <summary>
/// Marker interface for a command (write operation).
/// Commands are CQRS write operations that change state.
/// </summary>
public interface ICommand<out TResponse> : IRequest<TResponse>
    where TResponse : notnull
{
}