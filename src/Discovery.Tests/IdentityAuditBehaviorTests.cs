using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Users.Commands;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Cqrs.Behaviors;
using MediatR;

namespace Discovery.Tests;

/// <summary>
/// Auditoria de mutações de identidade (achado P0-4 da auditoria de 2026-11-07).
///
/// Antes, <c>IAuthAuditLogRepository</c> era chamado em UM único lugar
/// (UserAuthService, fluxo de login). Criar/editar/excluir usuário, reset de senha,
/// revogar MFA, CRUD de papel/permissão/grupo e — o mais grave — ATRIBUIÇÃO DE
/// PAPEL+ESCOPO não geravam registro algum. Um atacante que se autoconcedia papel
/// Global não deixava rastro.
///
/// Estes testes fixam o contrato do <see cref="IdentityAuditBehavior"/>, que é o
/// ponto único por onde as mutações passam.
/// </summary>
[TestFixture]
public class IdentityAuditBehaviorTests
{
    private static readonly Guid Actor = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Target = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Test]
    public async Task Records_a_user_creation_as_success()
    {
        var audit = new RecordingAuditService();
        var behavior = new IdentityAuditBehavior<CreateUserCommand, Result<UserDto>>(
            audit, new ActorStub(Actor, "ana.admin"));

        RequestHandlerDelegate<Result<UserDto>> next =
            _ => Task.FromResult(Result<UserDto>.Success(
                new UserDto(Guid.NewGuid(), "joao", "joao@ex.com", "Joao", true, true, false, false, false,
                    DateTime.UtcNow, DateTime.UtcNow, null)));
        await behavior.Handle(
            new CreateUserCommand("joao", "joao@ex.com", "Joao", "SenhaForte1!", true),
            next, CancellationToken.None);

        Assert.That(audit.Records, Has.Count.EqualTo(1));
        var record = audit.Records[0];
        Assert.That(record.EventType, Is.EqualTo("identity.user_created"));
        Assert.That(record.ActorUserId, Is.EqualTo(Actor));
        Assert.That(record.ActorLogin, Is.EqualTo("ana.admin"));
        Assert.That(record.TargetType, Is.EqualTo("User"));
        Assert.That(record.Success, Is.True);
    }

    [Test]
    public async Task Never_records_the_password_of_a_created_user()
    {
        var audit = new RecordingAuditService();
        var behavior = new IdentityAuditBehavior<CreateUserCommand, Result<UserDto>>(
            audit, new ActorStub(Actor, "ana.admin"));

        const string password = "SenhaSuperSecreta1!";
        RequestHandlerDelegate<Result<UserDto>> next =
            _ => Task.FromResult(Result<UserDto>.Failure(Error.Conflict("stub")));
        await behavior.Handle(
            new CreateUserCommand("joao", "joao@ex.com", "Joao", password, true),
            next, CancellationToken.None);

        var after = audit.Records[0].After?.ToString() ?? string.Empty;
        Assert.That(after, Does.Not.Contain(password), "a senha não pode ir para a tabela de auditoria");
        Assert.That(after, Does.Contain("joao"));
    }

    [Test]
    public async Task Never_records_password_reset_secrets()
    {
        var audit = new RecordingAuditService();
        var behavior = new IdentityAuditBehavior<Discovery.Core.Cqrs.Auth.Commands.ResetUserPasswordCommand, Result<VoidResult>>(
            audit, new ActorStub(Actor, "ana.admin"));

        RequestHandlerDelegate<Result<VoidResult>> next =
            _ => Task.FromResult(Result<VoidResult>.Success(VoidResult.Value));
        await behavior.Handle(
            new Discovery.Core.Cqrs.Auth.Commands.ResetUserPasswordCommand(Target, "NovaSenha123!", "ana.admin"),
            next, CancellationToken.None);

        var after = audit.Records[0].After?.ToString() ?? string.Empty;
        Assert.That(after, Does.Not.Contain("NovaSenha123!"));
        Assert.That(audit.Records[0].EventType, Is.EqualTo("identity.user_password_reset"));
    }

    [Test]
    public async Task Records_a_refused_scope_grant_as_a_failure()
    {
        var audit = new RecordingAuditService();
        var behavior = new IdentityAuditBehavior<Discovery.Core.Cqrs.UserGroups.Commands.AssignGroupRoleCommand, Result<Discovery.Core.DTOs.Groups.GroupRoleAssignmentDto>>(
            audit, new ActorStub(Actor, "admin.clienteA"));

        var cmd = new Discovery.Core.Cqrs.UserGroups.Commands.AssignGroupRoleCommand(
            Guid.NewGuid(), Guid.NewGuid(), Discovery.Core.Enums.Identity.ScopeLevel.Global, null, Actor);

        RequestHandlerDelegate<Result<Discovery.Core.DTOs.Groups.GroupRoleAssignmentDto>> next =
            _ => Task.FromResult(Result<Discovery.Core.DTOs.Groups.GroupRoleAssignmentDto>.Failure(
                Error.Forbidden("Você não pode conceder acesso fora do seu escopo.")));
        await behavior.Handle(cmd, next, CancellationToken.None);

        Assert.That(audit.Records, Has.Count.EqualTo(1));
        var record = audit.Records[0];
        // Uma tentativa RECUSADA de escalar privilégio é exatamente o evento que
        // interessa em segurança — precisa ficar registrada.
        Assert.That(record.Success, Is.False);
        Assert.That(record.EventType, Is.EqualTo("identity.role_scope_granted"));
        Assert.That(record.FailureReason, Does.Contain("escopo"));
    }

    [Test]
    public async Task Ignores_commands_that_are_not_identity_mutations()
    {
        var audit = new RecordingAuditService();
        var behavior = new IdentityAuditBehavior<NotAnIdentityCommand, Result<VoidResult>>(
            audit, new ActorStub(Actor, "ana.admin"));

        RequestHandlerDelegate<Result<VoidResult>> next =
            _ => Task.FromResult(Result<VoidResult>.Success(VoidResult.Value));
        await behavior.Handle(new NotAnIdentityCommand(), next, CancellationToken.None);

        Assert.That(audit.Records, Is.Empty);
    }

    [Test]
    public async Task Falls_back_to_the_actor_declared_in_the_command()
    {
        // Execução sem sessão HTTP (job/seed): o ator vem do próprio comando.
        var audit = new RecordingAuditService();
        var behavior = new IdentityAuditBehavior<Discovery.Core.Cqrs.UserGroups.Commands.AssignGroupRoleCommand, Result<Discovery.Core.DTOs.Groups.GroupRoleAssignmentDto>>(
            audit, new ActorStub(Guid.Empty, null));

        var cmd = new Discovery.Core.Cqrs.UserGroups.Commands.AssignGroupRoleCommand(
            Guid.NewGuid(), Guid.NewGuid(), Discovery.Core.Enums.Identity.ScopeLevel.Client, Guid.NewGuid(), Actor);

        RequestHandlerDelegate<Result<Discovery.Core.DTOs.Groups.GroupRoleAssignmentDto>> next =
            _ => Task.FromResult(Result<Discovery.Core.DTOs.Groups.GroupRoleAssignmentDto>.Success(
                new Discovery.Core.DTOs.Groups.GroupRoleAssignmentDto
                {
                    AssignmentId = Guid.NewGuid(),
                    RoleId = Guid.NewGuid(),
                    RoleName = "Support",
                    ScopeLevel = Discovery.Core.Enums.Identity.ScopeLevel.Client
                }));
        await behavior.Handle(cmd, next, CancellationToken.None);

        Assert.That(audit.Records[0].ActorUserId, Is.EqualTo(Actor));
    }

    [Test]
    public void Registry_covers_every_identity_command_the_critical_paths_use()
    {
        // Guarda contra regressão silenciosa: se alguém adicionar um comando de
        // identidade e esquecer de mapear, este teste falha.
        var expected = new[]
        {
            typeof(CreateUserCommand), typeof(UpdateUserCommand), typeof(DeleteUserCommand),
            typeof(Discovery.Core.Cqrs.Auth.Commands.ResetUserPasswordCommand),
            typeof(ForcePasswordChangeCommand), typeof(RevokeUserMfaCommand), typeof(RevokeUserMfaKeyCommand),
            typeof(Discovery.Core.Cqrs.Roles.Commands.CreateRoleCommand),
            typeof(Discovery.Core.Cqrs.Roles.Commands.UpdateRoleCommand),
            typeof(Discovery.Core.Cqrs.Roles.Commands.DeleteRoleCommand),
            typeof(Discovery.Core.Cqrs.Roles.Commands.AddRolePermissionCommand),
            typeof(Discovery.Core.Cqrs.Roles.Commands.RemoveRolePermissionCommand),
            typeof(Discovery.Core.Cqrs.UserGroups.Commands.CreateUserGroupCommand),
            typeof(Discovery.Core.Cqrs.UserGroups.Commands.UpdateUserGroupCommand),
            typeof(Discovery.Core.Cqrs.UserGroups.Commands.DeleteUserGroupCommand),
            typeof(Discovery.Core.Cqrs.UserGroups.Commands.AddGroupMemberCommand),
            typeof(Discovery.Core.Cqrs.UserGroups.Commands.RemoveGroupMemberCommand),
            typeof(Discovery.Core.Cqrs.UserGroups.Commands.AssignGroupRoleCommand),
            typeof(Discovery.Core.Cqrs.UserGroups.Commands.RemoveGroupRoleAssignmentCommand)
        };

        foreach (var commandType in expected)
        {
            var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(commandType);
            Assert.That(IdentityAuditRegistry.Describe(instance), Is.Not.Null,
                $"comando de identidade sem mapeamento de auditoria: {commandType.Name}");
        }
    }

    [Test]
    public async Task Records_the_attempt_and_rethrows_when_the_handler_throws()
    {
        // Branch mais delicada do behavior: o handler lança (ex.: falha de banco).
        // A transação reverte, então NADA foi persistido — mas a TENTATIVA é um dado
        // de segurança e o erro NÃO pode ser engolido (o chamador precisa do 500).
        var audit = new RecordingAuditService();
        var behavior = new IdentityAuditBehavior<DeleteUserCommand, Result<VoidResult>>(
            audit, new ActorStub(Actor, "ana.admin"));

        RequestHandlerDelegate<Result<VoidResult>> next =
            _ => throw new InvalidOperationException("db down");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => behavior.Handle(new DeleteUserCommand(Target), next, CancellationToken.None));

        Assert.That(audit.Records, Has.Count.EqualTo(1));
        Assert.That(audit.Records[0].Success, Is.False);
        Assert.That(audit.Records[0].FailureReason, Is.EqualTo(nameof(InvalidOperationException)));
    }

    [Test]
    public void Every_audited_command_returns_a_Result_so_the_outcome_can_be_read()
    {
        // O behavior lê sucesso/falha por reflexão sobre Result<T> (ReadOutcome). Se um
        // comando de identidade passasse a devolver outro tipo, ele seria auditado
        // como SUCESSO sempre — inclusive quando recusado — e o registro perderia o
        // valor (uma tentativa negada de escalar privilégio viraria "sucesso").
        var audited = new[]
        {
            typeof(CreateUserCommand), typeof(UpdateUserCommand), typeof(DeleteUserCommand),
            typeof(Discovery.Core.Cqrs.Auth.Commands.ResetUserPasswordCommand),
            typeof(Discovery.Core.Cqrs.Auth.Commands.ChangeUserPasswordCommand),
            typeof(ForcePasswordChangeCommand), typeof(RevokeUserMfaCommand), typeof(RevokeUserMfaKeyCommand),
            typeof(UpdateMyProfileCommand),
            typeof(Discovery.Core.Cqrs.Roles.Commands.CreateRoleCommand),
            typeof(Discovery.Core.Cqrs.Roles.Commands.UpdateRoleCommand),
            typeof(Discovery.Core.Cqrs.Roles.Commands.DeleteRoleCommand),
            typeof(Discovery.Core.Cqrs.Roles.Commands.AddRolePermissionCommand),
            typeof(Discovery.Core.Cqrs.Roles.Commands.RemoveRolePermissionCommand),
            typeof(Discovery.Core.Cqrs.UserGroups.Commands.CreateUserGroupCommand),
            typeof(Discovery.Core.Cqrs.UserGroups.Commands.UpdateUserGroupCommand),
            typeof(Discovery.Core.Cqrs.UserGroups.Commands.DeleteUserGroupCommand),
            typeof(Discovery.Core.Cqrs.UserGroups.Commands.AddGroupMemberCommand),
            typeof(Discovery.Core.Cqrs.UserGroups.Commands.RemoveGroupMemberCommand),
            typeof(Discovery.Core.Cqrs.UserGroups.Commands.AssignGroupRoleCommand),
            typeof(Discovery.Core.Cqrs.UserGroups.Commands.RemoveGroupRoleAssignmentCommand)
        };

        foreach (var commandType in audited)
        {
            var resultInterface = commandType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(Discovery.Core.Cqrs.ICommand<>));

            Assert.That(resultInterface, Is.Not.Null, $"{commandType.Name} não é ICommand<>");

            var responseType = resultInterface!.GetGenericArguments()[0];
            Assert.That(responseType.IsGenericType && responseType.GetGenericTypeDefinition() == typeof(Result<>),
                Is.True,
                $"{commandType.Name} devolve {responseType.Name}; o behavior só sabe ler desfecho de Result<T> " +
                "(auditaria como sucesso sempre, inclusive em recusa).");
        }
    }

    private sealed record NotAnIdentityCommand : ICommand<Result<VoidResult>>;

    private sealed record AuditRecord(
        string EventType, Guid ActorUserId, string? ActorLogin, string TargetType,
        Guid? TargetId, object? After, bool Success, string? FailureReason);

    private sealed class RecordingAuditService : IIdentityAuditService
    {
        public List<AuditRecord> Records { get; } = [];

        public Task RecordAsync(string eventType, Guid actorUserId, string? actorLogin, string targetType,
            Guid? targetId = null, Guid? targetUserId = null, object? before = null, object? after = null,
            bool success = true, string? failureReason = null)
        {
            Records.Add(new AuditRecord(eventType, actorUserId, actorLogin, targetType, targetId, after, success, failureReason));
            return Task.CompletedTask;
        }

        public Task<IEnumerable<Discovery.Core.Entities.Security.AuthAuditLog>> GetByActorAsync(Guid actorUserId, int limit = 100)
            => Task.FromResult<IEnumerable<Discovery.Core.Entities.Security.AuthAuditLog>>([]);
        public Task<IEnumerable<Discovery.Core.Entities.Security.AuthAuditLog>> GetByTargetAsync(string targetType, Guid targetId, int limit = 100)
            => Task.FromResult<IEnumerable<Discovery.Core.Entities.Security.AuthAuditLog>>([]);
        public Task<IEnumerable<Discovery.Core.Entities.Security.AuthAuditLog>> GetRecentIdentityAsync(string? eventTypePrefix = null, int limit = 200)
            => Task.FromResult<IEnumerable<Discovery.Core.Entities.Security.AuthAuditLog>>([]);
        public Task<IEnumerable<Discovery.Core.Entities.Security.AuthAuditLog>> GetByUserAsyncForAuditView(Guid userId, int limit = 100)
            => Task.FromResult<IEnumerable<Discovery.Core.Entities.Security.AuthAuditLog>>([]);
    }

    private sealed class ActorStub(Guid userId, string? login) : ICurrentActorAccessor
    {
        public Guid UserId { get; } = userId;
        public string? Login { get; } = login;
    }
}