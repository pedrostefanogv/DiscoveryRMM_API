using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Mfa;
using Discovery.Core.Cqrs.Mfa.Queries;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Entities.Security;
using Discovery.Core.Enums.Security;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Mfa;

public sealed class ListMfaKeysQueryHandler(IUserMfaKeyRepository repo) : IRequestHandler<ListMfaKeysQuery, Result<IReadOnlyList<MfaKeyDto>>>
{
    public async Task<Result<IReadOnlyList<MfaKeyDto>>> Handle(ListMfaKeysQuery q, CancellationToken ct)
    {
        var keys = await repo.GetActiveByUserIdAsync(q.UserId);
        var items = keys.Select(k => new MfaKeyDto(k.Id, k.UserId, k.KeyType.ToString(), k.Name, k.IsActive, k.CreatedAt, k.LastUsedAt)).ToList().AsReadOnly();
        return Result<IReadOnlyList<MfaKeyDto>>.Success(items);
    }
}

public sealed class BeginFido2RegistrationQueryHandler(
    IFido2Service fido2Service,
    IUserRepository userRepo,
    IUserMfaKeyRepository mfaKeyRepo
) : IRequestHandler<BeginFido2RegistrationQuery, Result<BeginFido2RegistrationResult>>
{
    public async Task<Result<BeginFido2RegistrationResult>> Handle(BeginFido2RegistrationQuery q, CancellationToken ct)
    {
        try
        {
            var user = await userRepo.GetByIdAsync(q.UserId);
            if (user is null)
                return Result<BeginFido2RegistrationResult>.Failure(Error.NotFound("Usuário não encontrado."));

            var activeKeys = await mfaKeyRepo.GetActiveByUserIdAsync(q.UserId);
            var existingCredentialIds = activeKeys
                .Where(k => k.KeyType == MfaKeyType.Fido2 && !string.IsNullOrWhiteSpace(k.CredentialIdBase64))
                .Select(k => k.CredentialIdBase64!)
                .ToList();

            var optionsJson = await fido2Service.BeginRegistrationAsync(
                q.UserId, user.Email, user.FullName, existingCredentialIds);

            return Result<BeginFido2RegistrationResult>.Success(new BeginFido2RegistrationResult(optionsJson));
        }
        catch (Exception ex)
        {
            return Result<BeginFido2RegistrationResult>.Failure(Error.Internal(ex.Message));
        }
    }
}

public sealed class CompleteFido2RegistrationCommandHandler(
    IFido2Service fido2Service,
    IUserMfaKeyRepository mfaKeyRepo,
    IUserRepository userRepo
) : IRequestHandler<CompleteFido2RegistrationCommand, Result<CompleteFido2RegistrationResult>>
{
    public async Task<Result<CompleteFido2RegistrationResult>> Handle(CompleteFido2RegistrationCommand cmd, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cmd.AttestationResponseJson))
                return Result<CompleteFido2RegistrationResult>.Failure(Error.Validation("attestationResponseJson", "Resposta de attestation é obrigatoria."));

            var result = await fido2Service.CompleteRegistrationAsync(cmd.UserId, cmd.AttestationResponseJson);
            if (!result.Success)
                return Result<CompleteFido2RegistrationResult>.Failure(Error.Unauthorized(result.ErrorMessage ?? "Registro FIDO2 inválido."));

            var key = new UserMfaKey
            {
                Id = Guid.NewGuid(),
                UserId = cmd.UserId,
                KeyType = MfaKeyType.Fido2,
                Name = string.IsNullOrWhiteSpace(cmd.KeyName) ? "Chave FIDO2" : cmd.KeyName.Trim(),
                IsActive = true,
                CredentialIdBase64 = result.CredentialIdBase64,
                PublicKeyBase64 = result.PublicKeyBase64,
                SignCount = result.SignCount,
                AaguidBase64 = result.AaguidBase64,
                UserHandleBase64 = result.UserHandleBase64,
                CreatedAt = DateTime.UtcNow
            };

            var created = await mfaKeyRepo.CreateAsync(key);
            await userRepo.SetMfaConfiguredAsync(cmd.UserId, true);

            return Result<CompleteFido2RegistrationResult>.Success(
                new CompleteFido2RegistrationResult(created.Id, "Chave FIDO2 registrada com sucesso."));
        }
        catch (Exception ex)
        {
            return Result<CompleteFido2RegistrationResult>.Failure(Error.Internal(ex.Message));
        }
    }
}
