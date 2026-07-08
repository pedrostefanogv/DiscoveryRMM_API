using System.Text.Json.Serialization;

namespace Discovery.Core.Cqrs;

/// <summary>
/// Represents the result of a command or query execution.
/// Encapsulates success/failure state with typed response and errors.
/// </summary>
public readonly record struct Result<TResponse>
    where TResponse : notnull
{
    /// <summary>
    /// Whether the operation succeeded.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Whether the operation failed.
    /// </summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// The response value on success.
    /// </summary>
    public TResponse? Value { get; }

    /// <summary>
    /// The list of errors on failure.
    /// </summary>
    public IReadOnlyList<Error> Errors { get; }

    [JsonConstructor]
    private Result(bool isSuccess, TResponse? value, IReadOnlyList<Error>? errors)
    {
        IsSuccess = isSuccess;
        Value = value;
        Errors = errors ?? [];
    }

    /// <summary>
    /// Creates a successful result with the given value.
    /// </summary>
    public static Result<TResponse> Success(TResponse value)
        => new(true, value, null);

    /// <summary>
    /// Creates a failure result with the given errors.
    /// </summary>
    public static Result<TResponse> Failure(IReadOnlyList<Error> errors)
        => new(false, default, errors);

    /// <summary>
    /// Creates a failure result with a single error.
    /// </summary>
    public static Result<TResponse> Failure(Error error)
        => new(false, default, [error]);

    /// <summary>
    /// Matches the result into a single output value.
    /// </summary>
    public TOutput Match<TOutput>(
        Func<TResponse, TOutput> success,
        Func<IReadOnlyList<Error>, TOutput> failure)
        => IsSuccess ? success(Value!) : failure(Errors);

    /// <summary>
    /// Executes an action based on the result state.
    /// </summary>
    public void Switch(
        Action<TResponse> onSuccess,
        Action<IReadOnlyList<Error>>? onFailure = null)
    {
        if (IsSuccess)
            onSuccess(Value!);
        else
            onFailure?.Invoke(Errors);
    }
}