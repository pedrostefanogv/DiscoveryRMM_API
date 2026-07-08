using Discovery.Core.Cqrs;
using FluentValidation;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Behaviors;

/// <summary>
/// Pipeline behavior that automatically validates commands using FluentValidation validators.
/// Only applies to ICommand requests.
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators
) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICommand<TResponse>
    where TResponse : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!validators.Any())
            return await next();

        var context = new ValidationContext<TRequest>(request);
        var failures = (await Task.WhenAll(
            validators.Select(v => v.ValidateAsync(context, cancellationToken))))
            .SelectMany(r => r.Errors)
            .Where(e => e is not null)
            .ToList();

        if (failures.Count == 0)
            return await next();

        var errors = failures
            .Select(f => Error.Validation(f.PropertyName, f.ErrorMessage))
            .ToList();

        // Return a failure Result<TResponse>
        var resultType = typeof(Result<>).MakeGenericType(typeof(TResponse));
        var failureMethod = resultType.GetMethod("Failure", [typeof(IReadOnlyList<Error>)]);
        var result = failureMethod!.Invoke(null, [errors]);

        return (TResponse)result!;
    }
}