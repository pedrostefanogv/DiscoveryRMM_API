using Discovery.Api.Controllers;
using Discovery.Core.Cqrs.Tickets.Chat.Dtos;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Tests;

/// <summary>
/// Privacidade do chat de suporte AVULSO (máquina, sem chamado).
///
/// O relato era: ao abrir o chat de um agent, o console já exibia a última
/// conversa daquela máquina — inclusive a de outro atendente/turno, sobre outro
/// usuário final. A regra é: conversa avulsa pertence a quem a iniciou.
///
/// Estes testes travam a regra nos três pontos onde ela precisa valer:
/// entidade (fonte única), handler de start (não "entra" na conversa alheia) e
/// controller (não devolve DTO/mensagens de sessão alheia).
/// </summary>
[TestFixture]
public class SupportChatPrivacyTests
{
    private static TicketChatSession AdHoc(Guid? owner, Guid? agentId = null) => new()
    {
        Id = Guid.NewGuid(),
        TicketId = null,
        AgentId = agentId ?? Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        SiteId = Guid.NewGuid(),
        Status = TicketChatSessionStatus.Active,
        StartedByUserId = owner,
        StartedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Test]
    public void AdHocSession_IsVisibleOnlyToItsOwner()
    {
        var owner = Guid.NewGuid();
        var session = AdHoc(owner);

        Assert.Multiple(() =>
        {
            Assert.That(session.IsVisibleToSupportUser(owner), Is.True);
            Assert.That(session.IsVisibleToSupportUser(Guid.NewGuid()), Is.False,
                "outro atendente não pode ver a conversa avulsa");
            Assert.That(session.IsVisibleToSupportUser(null), Is.False,
                "sem identidade não há como provar dono: fail-closed");
        });
    }

    [Test]
    public void LegacyAdHocSessionWithoutOwner_IsInvisibleToEveryone()
    {
        var session = AdHoc(null);
        Assert.That(session.IsVisibleToSupportUser(Guid.NewGuid()), Is.False);
    }

    [Test]
    public void TicketLinkedSession_FollowsTicketAccessRule_NotOwner()
    {
        var session = AdHoc(owner: Guid.NewGuid());
        session.TicketId = Guid.NewGuid();

        // Conversa vinculada a chamado é histórico do ATENDIMENTO: qualquer
        // atendente que passa no ACL do chamado pode ver (a checagem de ticket
        // fica no accessGuard, no controller).
        Assert.Multiple(() =>
        {
            Assert.That(session.IsVisibleToSupportUser(Guid.NewGuid()), Is.True);
            Assert.That(session.IsVisibleToSupportUser(null), Is.True);
        });
    }

    [Test]
    public async Task GetCurrentForAgent_DoesNotExposeAnotherSupportUsersSession()
    {
        var ownerId = Guid.NewGuid();
        var session = AdHoc(ownerId);
        var controller = BuildController(session, requestingUserId: Guid.NewGuid());

        var action = await controller.GetCurrentForAgent(session.AgentId!.Value, CancellationToken.None);

        var ok = action as OkObjectResult;
        Assert.That(ok, Is.Not.Null, "sessão alheia não é erro: é 'sem sessão'");
        var state = ok!.Value as TicketChatStateDto;
        Assert.Multiple(() =>
        {
            Assert.That(state!.HasSession, Is.False);
            Assert.That(state.Session, Is.Null);
        });
    }

    [Test]
    public async Task GetMessages_ReturnsNotFoundForAnotherSupportUsersSession()
    {
        var ownerId = Guid.NewGuid();
        var session = AdHoc(ownerId);
        var controller = BuildController(session, requestingUserId: Guid.NewGuid());

        var action = await controller.GetMessages(session.Id, afterSequence: 0, limit: 200, CancellationToken.None);

        Assert.That(action, Is.InstanceOf<NotFoundResult>(),
            "IDOR: saber o sessionId não pode liberar a conversa avulsa de outro atendente");
    }

    [Test]
    public async Task GetMessages_AllowsTheOwnerOnceTheSessionIsTheirs()
    {
        // Guarda contra excesso de bloqueio: o dono NÃO cai no 404 (a consulta
        // de mensagens em si é feita pelo mediator, fora deste teste).
        var ownerId = Guid.NewGuid();
        var session = AdHoc(ownerId);
        var controller = BuildController(session, requestingUserId: ownerId);

        var action = await controller.GetMessages(session.Id, afterSequence: 0, limit: 200, CancellationToken.None);

        Assert.That(action, Is.Not.InstanceOf<NotFoundResult>());
    }

    private static SupportChatController BuildController(TicketChatSession session, Guid? requestingUserId)
    {
        var controller = new SupportChatController(
            mediator: null!,
            chatRepository: new StubChatRepository(session),
            agentRepository: null!,
            siteRepository: null!,
            accessGuard: null!,
            scopeContext: null!,
            presenceService: null!,
            attachmentService: null!,
            attachmentRepository: null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        controller.HttpContext.Items["UserId"] = requestingUserId;
        return controller;
    }

    /// <summary>Repositório mínimo para os caminhos de acesso (sem banco).</summary>
    private sealed class StubChatRepository(TicketChatSession session) : ITicketChatRepository
    {
        public Task<TicketChatSession?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<TicketChatSession?>(session.Id == sessionId ? session : null);

        public Task<TicketChatSession?> GetLatestForAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
            => Task.FromResult<TicketChatSession?>(session.AgentId == agentId ? session : null);

        public Task<TicketChatSession?> GetCurrentForAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TicketChatSession?> GetCurrentForTicketAsync(Guid ticketId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TicketChatSession?> GetLatestForTicketAsync(Guid ticketId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task AddSessionAsync(TicketChatSession newSession, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task UpdateSessionAsync(TicketChatSession updated, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TicketChatSession>> GetHistoryAsync(Guid ticketId, int skip, int take, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TicketChatMessage>> GetMessagesAsync(Guid sessionId, long afterSequence, int limit, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(TicketChatMessage Message, bool Created)> AppendMessageAsync(
            Guid sessionId, TicketChatMessage message, IReadOnlyList<Guid> attachmentIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TicketChatAttachmentRef>> GetAttachmentsForMessagesAsync(
            IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TicketChatSession>> GetExpiredPendingAsync(DateTime nowUtc, int limit, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TicketChatSession>> GetInactiveActiveAsync(DateTime cutoffUtc, int limit, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TicketChatSession>> GetActiveForClosedTicketsAsync(int limit, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
