using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Discovery.Core.Cqrs.Tickets.Chat.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Identity;
using Discovery.Infrastructure.Cqrs.Tickets.Chat;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// O chat de suporte precisa mostrar o NOME do tecnico ("Pedro Stefano"), nao
/// o login ("pedrostefanogv"). Os claims do JWT carregam o login, entao o
/// servidor resolve o FullName do cadastro (cacheado) e cai para o fallback
/// (username ou "suporte") quando o cadastro nao tem nome.
/// </summary>
public class ChatUserNameResolverTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private sealed class FakeUserRepository(User? user) : IUserRepository
    {
        public int GetByIdCalls;
        public Task<User?> GetByIdAsync(Guid id)
        {
            Interlocked.Increment(ref GetByIdCalls);
            return Task.FromResult(user);
        }
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> ids) => Task.FromResult<IReadOnlyList<User>>(user is null ? [] : [user]);
        public Task<User?> GetByLoginAsync(string login) => Task.FromResult(user);
        public Task<User?> GetByEmailAsync(string email) => Task.FromResult(user);
        public Task<User?> GetByLoginOrEmailAsync(string loginOrEmail) => Task.FromResult(user);
        public Task<IReadOnlyList<User>> GetAllPageAsync(string? cursor, int take = 50) => Task.FromResult<IReadOnlyList<User>>([]);
        public Task<User> CreateAsync(User u) => Task.FromResult(u);
        public Task<User> UpdateAsync(User u) => Task.FromResult(u);
        public Task<bool> SetMfaConfiguredAsync(Guid userId, bool configured) => Task.FromResult(true);
        public Task<bool> SetLastLoginAsync(Guid userId, DateTime at) => Task.FromResult(true);
        public Task<bool> SetMfaFailureStateAsync(Guid userId, int failedAttempts, DateTime? lockoutUntil) => Task.FromResult(true);
        public Task<bool> ResetMfaFailureStateAsync(Guid userId) => Task.FromResult(true);
        public Task<bool> ExistsByLoginAsync(string login) => Task.FromResult(false);
        public Task<bool> ExistsByEmailAsync(string email) => Task.FromResult(false);
        public Task<int> CountAsync() => Task.FromResult(0);
    }

    private static ChatUserNameResolver BuildResolver(User? user, out FakeUserRepository repo)
    {
        repo = new FakeUserRepository(user);
        return new ChatUserNameResolver(repo, NullLogger<ChatUserNameResolver>.Instance, TimeProvider.System);
    }

    [Test]
    public async Task ResolveAsync_UserComFullName_DevolveNomeDeExibicao()
    {
        var resolver = BuildResolver(new User { Id = UserId, Login = "pedrostefanogv", FullName = "Pedro Stefano" }, out _);
        var name = await resolver.ResolveAsync(UserId, "pedrostefanogv");
        Assert.That(name, Is.EqualTo("Pedro Stefano"));
    }

    [Test]
    public async Task ResolveAsync_SemFullName_CaiParaLoginDoFallback()
    {
        var resolver = BuildResolver(new User { Id = UserId, Login = "pedrostefanogv", FullName = "" }, out _);
        var name = await resolver.ResolveAsync(UserId, "pedrostefanogv");
        Assert.That(name, Is.EqualTo("pedrostefanogv"));
    }

    [Test]
    public async Task ResolveAsync_UsuarioInexistente_CaiParaFallback()
    {
        var resolver = BuildResolver(null, out _);
        var name = await resolver.ResolveAsync(UserId, "pedrostefanogv");
        Assert.That(name, Is.EqualTo("pedrostefanogv"));
    }

    [Test]
    public async Task ResolveAsync_SemUserIdEFallbackVazio_DevolveSuporte()
    {
        var resolver = BuildResolver(null, out _);
        var name = await resolver.ResolveAsync(null, "");
        Assert.That(name, Is.EqualTo("suporte"));
    }

    [Test]
    public async Task ResolveAsync_Cacheia_NaoConsultaBancoAPorMensagem()
    {
        var resolver = BuildResolver(new User { Id = UserId, Login = "pedrostefanogv", FullName = "Pedro Stefano" }, out var repo);
        for (var i = 0; i < 5; i++)
            await resolver.ResolveAsync(UserId, "pedrostefanogv");
        Assert.That(repo.GetByIdCalls, Is.EqualTo(1), "o cache evita N+1 por mensagem");
    }

    [Test]
    public async Task ResolveAsync_NomeVazioNoCadastro_CacheNegativoNaoConsultaDeNovo()
    {
        var resolver = BuildResolver(new User { Id = UserId, Login = "pedrostefanogv", FullName = "  " }, out var repo);
        await resolver.ResolveAsync(UserId, "pedrostefanogv");
        await resolver.ResolveAsync(UserId, "pedrostefanogv");
        Assert.That(repo.GetByIdCalls, Is.EqualTo(1), "cache negativo evita reconsulta imediata");
    }

    [Test]
    public async Task ResolveAsync_RepositorioFalhando_CaiParaFallbackSemExcecao()
    {
        var repo = new ThrowingUserRepository();
        var resolver = new ChatUserNameResolver(repo, NullLogger<ChatUserNameResolver>.Instance, TimeProvider.System);
        var name = await resolver.ResolveAsync(UserId, "pedrostefanogv");
        Assert.That(name, Is.EqualTo("pedrostefanogv"));
    }

    private sealed class ThrowingUserRepository : IUserRepository
    {
        public Task<User?> GetByIdAsync(Guid id) => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> ids) => throw new InvalidOperationException("boom");
        public Task<User?> GetByLoginAsync(string login) => throw new InvalidOperationException("boom");
        public Task<User?> GetByEmailAsync(string email) => throw new InvalidOperationException("boom");
        public Task<User?> GetByLoginOrEmailAsync(string loginOrEmail) => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<User>> GetAllPageAsync(string? cursor, int take = 50) => throw new InvalidOperationException("boom");
        public Task<User> CreateAsync(User u) => throw new InvalidOperationException("boom");
        public Task<User> UpdateAsync(User u) => throw new InvalidOperationException("boom");
        public Task<bool> SetMfaConfiguredAsync(Guid userId, bool configured) => throw new InvalidOperationException("boom");
        public Task<bool> SetLastLoginAsync(Guid userId, DateTime at) => throw new InvalidOperationException("boom");
        public Task<bool> SetMfaFailureStateAsync(Guid userId, int failedAttempts, DateTime? lockoutUntil) => throw new InvalidOperationException("boom");
        public Task<bool> ResetMfaFailureStateAsync(Guid userId) => throw new InvalidOperationException("boom");
        public Task<bool> ExistsByLoginAsync(string login) => throw new InvalidOperationException("boom");
        public Task<bool> ExistsByEmailAsync(string email) => throw new InvalidOperationException("boom");
        public Task<int> CountAsync() => throw new InvalidOperationException("boom");
    }
}


/// <summary>
/// Contrato no HANDLER: a mensagem do atendente grava o nome de EXIBICAO
/// (resolvido do cadastro), nao o login que veio no comando. A mensagem do
/// usuario final mantem a autoria informada (hostname da maquina).
/// </summary>
public class TicketChatAuthorNameResolutionTests
{
    private sealed class FixedNameResolver(string name) : IChatUserNameResolver
    {
        public Task<string> ResolveAsync(Guid? userId, string fallback, CancellationToken cancellationToken = default)
            => Task.FromResult(name);
    }

    private sealed class FakeTicketChatRepository : ITicketChatRepository
    {
        public TicketChatMessage? Saved { get; private set; }

        public Task<TicketChatSession?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<TicketChatSession?>(new TicketChatSession
            {
                Id = sessionId,
                Status = TicketChatSessionStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        public Task<TicketChatSession?> GetCurrentForTicketAsync(Guid ticketId, CancellationToken cancellationToken = default)
            => Task.FromResult<TicketChatSession?>(null);
        public Task<TicketChatSession?> GetCurrentForAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
            => Task.FromResult<TicketChatSession?>(null);
        public Task<TicketChatSession?> GetLatestForTicketAsync(Guid ticketId, CancellationToken cancellationToken = default)
            => Task.FromResult<TicketChatSession?>(null);
        public Task<TicketChatSession?> GetLatestForAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
            => Task.FromResult<TicketChatSession?>(null);
        public Task AddSessionAsync(TicketChatSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateSessionAsync(TicketChatSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TicketChatSession>> GetHistoryAsync(Guid ticketId, int skip, int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TicketChatSession>>([]);
        public Task<IReadOnlyList<TicketChatMessage>> GetMessagesAsync(Guid sessionId, long afterSequence, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TicketChatMessage>>([]);
        public Task<(TicketChatMessage Message, bool Created)> AppendMessageAsync(
            Guid sessionId, TicketChatMessage message, IReadOnlyList<Guid> attachmentIds, CancellationToken cancellationToken = default)
        {
            Saved = message;
            var persisted = new TicketChatMessage
            {
                Id = message.Id,
                SessionId = sessionId,
                SequenceNumber = 1,
                ClientMessageId = message.ClientMessageId,
                AuthorKind = message.AuthorKind,
                AuthorUserId = message.AuthorUserId,
                AuthorName = message.AuthorName,
                Body = message.Body,
                CreatedAt = message.CreatedAt
            };
            return Task.FromResult((persisted, true));
        }
        public Task<IReadOnlyList<TicketChatAttachmentRef>> GetAttachmentsForMessagesAsync(
            IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TicketChatAttachmentRef>>([]);
        public Task<IReadOnlyList<TicketChatSession>> GetExpiredPendingAsync(DateTime nowUtc, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TicketChatSession>>([]);
        public Task<IReadOnlyList<TicketChatSession>> GetInactiveActiveAsync(DateTime cutoffUtc, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TicketChatSession>>([]);
        public Task<IReadOnlyList<TicketChatSession>> GetActiveForClosedTicketsAsync(int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TicketChatSession>>([]);
    }

    private sealed class RecordingEnvelopePublisher : ITicketChatEnvelopePublisher
    {
        public Task PublishSessionOpenedAsync(TicketChatSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishSessionEndedAsync(TicketChatSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishSessionDeclinedAsync(TicketChatSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishSessionStatusSignalAsync(TicketChatSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishActivitySignalAsync(TicketChatSession session, TicketChatMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishMessageToAgentAsync(TicketChatSession session, TicketChatMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopNotificationService : INotificationService
    {
        public Task<AppNotification> PublishAsync(NotificationPublishRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new AppNotification());
        public Task<IReadOnlyList<AppNotification>> GetRecentAsync(Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null, string? topic = null, NotificationSeverity? severity = null, bool? isRead = null, int limit = 50)
            => Task.FromResult<IReadOnlyList<AppNotification>>([]);
        public Task<bool> MarkAsReadAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null)
            => Task.FromResult(false);
        public Task<bool> DeleteAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null)
            => Task.FromResult(false);
    }

    private sealed class NotViewingPresence : ITicketChatPresenceService
    {
        public int TtlSeconds => 75;
        public Task TouchAsync(Guid userId, Guid ticketId, string? clientId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(Guid userId, Guid ticketId, string? clientId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsViewingAsync(Guid userId, Guid ticketId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task TouchSessionAsync(Guid userId, Guid sessionId, string? clientId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearSessionAsync(Guid userId, Guid sessionId, string? clientId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsViewingSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private static SendTicketChatMessageCommandHandler BuildHandler(FakeTicketChatRepository repository, string resolvedName)
        => new(
            repository,
            new RecordingEnvelopePublisher(),
            new NoopNotificationService(),
            new NotViewingPresence(),
            new FixedNameResolver(resolvedName),
            Microsoft.Extensions.Options.Options.Create(new Discovery.Core.Configuration.TicketChatOptions()),
            NullLogger<SendTicketChatMessageCommandHandler>.Instance);

    [Test]
    public async Task SendMessage_DoSuporte_GravaNomeDeExibicaoResolvido()
    {
        var repository = new FakeTicketChatRepository();
        var handler = BuildHandler(repository, "Pedro Stefano");

        var result = await handler.Handle(new SendTicketChatMessageCommand(
            Guid.NewGuid(), Guid.NewGuid(), "pedrostefanogv", "support", "Ola!", null, null), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True, result.IsFailure ? result.Errors[0].Message : null);
        Assert.That(repository.Saved!.AuthorName, Is.EqualTo("Pedro Stefano"));
    }

    [Test]
    public async Task SendMessage_DoUsuarioFinal_MantemAutoriaInformada()
    {
        var repository = new FakeTicketChatRepository();
        var handler = BuildHandler(repository, "Nunca Usado");

        var result = await handler.Handle(new SendTicketChatMessageCommand(
            Guid.NewGuid(), null, "PC-AORUSAXV2", "enduser", "Oi", null, null), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True, result.IsFailure ? result.Errors[0].Message : null);
        Assert.That(repository.Saved!.AuthorName, Is.EqualTo("PC-AORUSAXV2"));
    }
}
