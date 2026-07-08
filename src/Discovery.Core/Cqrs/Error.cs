namespace Discovery.Core.Cqrs;

/// <summary>
/// Represents a domain error with a code, message, and optional details.
/// </summary>
public sealed record Error
{
    /// <summary>
    /// Error code (e.g., "Validation", "NotFound", "Conflict", "Unauthorized").
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Optional field/property name that caused the error (for validation errors).
    /// </summary>
    public string? Field { get; }

    private Error(string code, string message, string? field = null)
    {
        Code = code;
        Message = message;
        Field = field;
    }

    public static Error Validation(string field, string message)
        => new("Validation", message, field);

    public static Error NotFound(string message)
        => new("NotFound", message);

    public static Error Conflict(string message)
        => new("Conflict", message);

    public static Error Unauthorized(string message)
        => new("Unauthorized", message);

    public static Error Forbidden(string message)
        => new("Forbidden", message);

    public static Error Internal(string message)
        => new("Internal", message);

    public override string ToString() => $"{Code}: {Message}";
}