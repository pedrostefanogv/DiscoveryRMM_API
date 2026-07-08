using MediatR;

namespace Discovery.Core.Cqrs;

/// <summary>
/// Marker interface for a query (read operation).
/// Queries are CQRS read operations that do not change state.
/// </summary>
public interface IQuery<out TResponse> : IRequest<TResponse>
    where TResponse : notnull
{
}