using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Tickets;
using Discovery.Infrastructure.Cqrs.Tickets.CommandHandlers;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Anexos de comentário (nível conversa) e soft delete de comentário.
///
/// Contratos verificados:
/// - o anexo de comentário é gravado com EntityType="TicketComment" e ligado ao
///   COMENTÁRIO (não ao chamado) — o repositório do chamado continua separado;
/// - a query de comentários devolve os anexos agrupados por comentário e o
///   comentário excluído continua legível, apenas marcado;
/// - excluir um comentário mantém o conteúdo, registra quem excluiu, faz soft
///   delete dos anexos e é idempotente;
/// - o comentário de OUTRO chamado não é aceito como pai do anexo (anti-IDOR).
/// </summary>
public class TicketCommentAttachmentTests
{
    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"ticket-comment-attachments-{Guid.NewGuid():N}")
            .Options;
        return new CommentAttachmentTestDbContext(options);
    }

    private static Ticket NewTicket() => new()
    {
        Id = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        Title = "Chamado com anexos",
        Description = "desc",
        WorkflowStateId = Guid.NewGuid(),
        Priority = TicketPriority.Medium,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static TicketComment NewComment(Guid ticketId, string content) => new()
    {
        Id = Guid.NewGuid(),
        TicketId = ticketId,
        Author = "Atendente",
        Content = content,
        IsInternal = false,
        CreatedAt = DateTime.UtcNow
    };

    private static Attachment NewCommentAttachment(Guid commentId, string fileName, Guid? clientId = null) => new()
    {
        Id = Guid.NewGuid(),
        EntityType = "TicketComment",
        EntityId = commentId,
        ClientId = clientId,
        FileName = fileName,
        StorageObjectKey = $"global/ticketcomment/{commentId:N}/attachments/{Guid.NewGuid():N}/{fileName}",
        StorageBucket = "discovery-files",
        ContentType = "image/png",
        SizeBytes = 2048,
        UploadedBy = "Atendente",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Test]
    public async Task GetComments_ReturnsAttachmentsGroupedPerComment_WithoutNPlusOne()
    {
        await using var db = CreateDb();
        var ticket = NewTicket();
        var withAttachment = NewComment(ticket.Id, "com print");
        var withoutAttachment = NewComment(ticket.Id, "sem anexo");
        db.AddRange(ticket, withAttachment, withoutAttachment);
        db.Attachments.AddRange(
            NewCommentAttachment(withAttachment.Id, "print-1.png"),
            NewCommentAttachment(withAttachment.Id, "print-2.png"));
        await db.SaveChangesAsync();

        var service = new TicketQueryService(db);
        var page = await service.GetCommentsAsync(ticket.Id, null, 50, includeInternal: true);

        Assert.That(page.Items, Has.Count.EqualTo(2));

        var withAttachments = page.Items.Single(c => c.Id == withAttachment.Id);
        Assert.That(withAttachments.Attachments, Is.Not.Null);
        Assert.That(withAttachments.Attachments!.Select(a => a.FileName),
            Is.EquivalentTo(new[] { "print-1.png", "print-2.png" }));

        var noAttachments = page.Items.Single(c => c.Id == withoutAttachment.Id);
        Assert.That(noAttachments.Attachments, Is.Null.Or.Empty);

        // Nenhum anexo do chamado (EntityType="Ticket") pode vazar para o comentário.
        db.Attachments.Add(new Attachment
        {
            Id = Guid.NewGuid(),
            EntityType = "Ticket",
            EntityId = ticket.Id,
            FileName = "repositorio.zip",
            ContentType = "application/zip",
            SizeBytes = 100,
            StorageObjectKey = "global/ticket/x/repositorio.zip",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var reloaded = await service.GetCommentsAsync(ticket.Id, null, 50, includeInternal: true);
        Assert.That(reloaded.Items.Single(c => c.Id == withAttachment.Id).Attachments!.Select(a => a.FileName),
            Is.EquivalentTo(new[] { "print-1.png", "print-2.png" }),
            "anexo do repositorio do chamado nao pode aparecer no comentario");
    }

    [Test]
    public async Task GetComment_RejectsCommentFromAnotherTicket()
    {
        await using var db = CreateDb();
        var ticket = NewTicket();
        var otherTicket = NewTicket();
        var comment = NewComment(otherTicket.Id, "comentario de outro chamado");
        db.AddRange(ticket, otherTicket, comment);
        db.Attachments.Add(NewCommentAttachment(comment.Id, "print.png"));
        await db.SaveChangesAsync();

        var handler = new GetTicketCommentQueryHandler(db);

        var ok = await handler.Handle(new GetTicketCommentQuery(otherTicket.Id, comment.Id), default);
        Assert.That(ok.IsSuccess, Is.True);
        Assert.That(ok.Value!.Attachments, Has.Count.EqualTo(1));

        // Mesmo GUID de comentário, chamado errado => não existe (anti-IDOR).
        var idor = await handler.Handle(new GetTicketCommentQuery(ticket.Id, comment.Id), default);
        Assert.That(idor.IsSuccess, Is.False);
    }

    [Test]
    public async Task DeleteComment_KeepsContent_MarksDeleted_AndSoftDeletesAttachments()
    {
        await using var db = CreateDb();
        var ticket = NewTicket();
        var comment = NewComment(ticket.Id, "conteudo que deve continuar legivel");
        db.AddRange(ticket, comment);
        db.Attachments.AddRange(
            NewCommentAttachment(comment.Id, "print.png"),
            NewCommentAttachment(comment.Id, "laudo.pdf"));
        await db.SaveChangesAsync();

        var activity = new RecordingActivityLogService();
        var handler = new DeleteTicketCommentCommandHandler(db, new AttachmentRepository(db), activity);
        var deletedByUserId = Guid.NewGuid();

        var result = await handler.Handle(
            new DeleteTicketCommentCommand(ticket.Id, comment.Id, deletedByUserId, "Maria"), default);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Content, Is.EqualTo("conteudo que deve continuar legivel"),
            "o conteudo permanece: comentario excluido continua legivel");
        Assert.That(result.Value.DeletedAt, Is.Not.Null);
        Assert.That(result.Value.DeletedBy, Is.EqualTo("Maria"));

        var stored = await db.TicketComments.AsNoTracking().SingleAsync(c => c.Id == comment.Id);
        Assert.That(stored.Content, Is.EqualTo("conteudo que deve continuar legivel"));
        Assert.That(stored.IsDeleted, Is.True);

        var attachments = await db.Attachments.AsNoTracking()
            .Where(a => a.EntityId == comment.Id).ToListAsync();
        Assert.That(attachments, Has.Count.EqualTo(2));
        Assert.That(attachments.All(a => a.IsDeleted), Is.True, "anexos do comentario sao soft-deletados junto");

        Assert.That(activity.LastType, Is.EqualTo(TicketActivityType.CommentDeleted));
        Assert.That(activity.LastChangedByUserId, Is.EqualTo(deletedByUserId));

        // Idempotente: segunda exclusão não regrava data nem duplica evento.
        var deletedAt = stored.DeletedAt;
        var calls = activity.Calls;
        var again = await handler.Handle(
            new DeleteTicketCommentCommand(ticket.Id, comment.Id, Guid.NewGuid(), "Outro"), default);

        Assert.That(again.IsSuccess, Is.True);
        Assert.That(activity.Calls, Is.EqualTo(calls), "exclusao repetida nao pode gerar novo evento");
        var storedAgain = await db.TicketComments.AsNoTracking().SingleAsync(c => c.Id == comment.Id);
        Assert.That(storedAgain.DeletedAt, Is.EqualTo(deletedAt));
    }

    [Test]
    public async Task DeleteComment_UnknownComment_ReturnsNotFound()
    {
        await using var db = CreateDb();
        var ticket = NewTicket();
        db.Add(ticket);
        await db.SaveChangesAsync();

        var handler = new DeleteTicketCommentCommandHandler(
            db, new AttachmentRepository(db), new RecordingActivityLogService());

        var result = await handler.Handle(
            new DeleteTicketCommentCommand(ticket.Id, Guid.NewGuid(), null, "Maria"), default);

        Assert.That(result.IsSuccess, Is.False);
    }

    private sealed class RecordingActivityLogService : IActivityLogService
    {
        public int Calls { get; private set; }
        public TicketActivityType? LastType { get; private set; }
        public Guid? LastChangedByUserId { get; private set; }

        public Task<TicketActivityLog> LogActivityAsync(
            Guid ticketId, TicketActivityType type, Guid? changedByUserId,
            string? oldValue = null, string? newValue = null, string? comment = null)
        {
            Calls++;
            LastType = type;
            LastChangedByUserId = changedByUserId;
            return Task.FromResult<TicketActivityLog>(null!);
        }

        public Task<TicketActivityLog> LogStateChangeAsync(Guid ticketId, Guid? changedByUserId, Guid oldStateId, Guid newStateId)
            => throw new NotSupportedException();

        public Task<TicketActivityLog> LogAssignmentAsync(Guid ticketId, Guid? changedByUserId, Guid? oldUserId, Guid? newUserId)
            => throw new NotSupportedException();

        public Task<TicketActivityLog> LogPriorityChangeAsync(Guid ticketId, Guid? changedByUserId, string oldPriority, string newPriority)
            => throw new NotSupportedException();

        public Task<TicketActivityLog> LogDepartmentChangeAsync(Guid ticketId, Guid? changedByUserId, string oldDept, string newDept)
            => throw new NotSupportedException();
    }

    private sealed class CommentAttachmentTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(Ticket), typeof(TicketComment), typeof(Attachment) };

            foreach (var entityType in typeof(Ticket).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Ticket>(e => { e.HasKey(t => t.Id); e.Ignore(t => t.DaysOpen); });
            modelBuilder.Entity<TicketComment>(e => { e.HasKey(c => c.Id); e.Ignore(c => c.IsDeleted); });
            modelBuilder.Entity<Attachment>(e => { e.HasKey(a => a.Id); e.Ignore(a => a.IsDeleted); });
        }
    }
}
