using System.Collections.Concurrent;
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
    // Fábricas de failure em cache por tipo de resposta. Sem cache, cada falha
    // de validação paginaria MakeGenericType + GetMethod + Invoke (reflexão por
    // request). TResponse é tipicamente Result<X> (ICommand<Result<Dto>>), então
    // o failure devolvido é Result<X>.Failure — o MESMO tipo de TResponse.
    private static readonly ConcurrentDictionary<Type, Func<IReadOnlyList<Error>, object>> FailureFactories = new();

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

        return BuildFailure(errors);
    }

    /// <summary>
    /// Constrói o failure de validação. Corrige dois defeitos da versão anterior:
    /// 1) ela criava Result&lt;TResponse&gt; (ex.: Result&lt;Result&lt;Dto&gt;&gt;) e o cast para
    ///    TResponse lançava InvalidCastException — toda falha de validação virava
    ///    HTTP 500 em vez do 400 com erros por campo;
    /// 2) para TResponse que NÃO é Result&lt;&gt;, não existe "valor de falha": lança
    ///    InvalidOperationException com as mensagens, que o
    ///    ExceptionHandlingMiddleware mapeia para 400 exibindo a mensagem.
    /// </summary>
    private static TResponse BuildFailure(IReadOnlyList<Error> errors)
    {
        var responseType = typeof(TResponse);
        if (responseType.IsGenericType && responseType.GetGenericTypeDefinition() == typeof(Result<>))
        {
            var factory = FailureFactories.GetOrAdd(responseType, static t =>
            {
                var valueType = t.GetGenericArguments()[0];
                var method = typeof(Result<>)
                    .MakeGenericType(valueType)
                    .GetMethod(nameof(Result<object>.Failure), new[] { typeof(IReadOnlyList<Error>) });
                return errors => method!.Invoke(null, new object[] { errors })!;
            });
            return (TResponse)factory(errors);
        }

        var messages = string.Join("; ", errors.Select(e => e.Message));
        throw new InvalidOperationException($"Requisição inválida: {messages}");
    }
}