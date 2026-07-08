using Discovery.Core.Cqrs;
using Discovery.Core.DTOs.Auth;

namespace Discovery.Core.Cqrs.Auth.Queries;

public sealed record BeginFido2AssertionQuery(Guid UserId) : IQuery<Result<BeginFido2AssertionResult>>;
public sealed record BeginFido2AssertionResult(string OptionsJson);

public sealed record GetFirstAccessStatusQuery(Guid UserId) : IQuery<Result<FirstAccessStatusDto>>;
