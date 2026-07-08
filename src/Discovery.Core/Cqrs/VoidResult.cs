namespace Discovery.Core.Cqrs;

/// <summary>
/// Represents a void/success result for commands that don't return data.
/// Distinct from MediatR.Unit to avoid ambiguity.
/// </summary>
public sealed record VoidResult
{
    public static readonly VoidResult Value = new();
    private VoidResult() { }
}
