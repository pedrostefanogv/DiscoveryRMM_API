using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Montagem da descrição legível e resolução de rótulos da timeline de auditoria.
/// Garante que nenhum evento fique "só com a data" (tipo/valores sem tradução).
/// </summary>
public class TicketTimelineMapperTests
{
    private static TicketActivityLog Log(
        TicketActivityType type,
        string? oldValue = null,
        string? newValue = null,
        string? comment = null,
        Guid? changedBy = null)
        => new()
        {
            Id = Guid.NewGuid(),
            TicketId = Guid.NewGuid(),
            Type = type,
            OldValue = oldValue,
            NewValue = newValue,
            Comment = comment,
            ChangedByUserId = changedBy,
            CreatedAt = DateTime.UtcNow
        };

    [Test]
    public void BuildDescription_ShouldDescribeCreated()
    {
        Assert.That(
            TicketTimelineMapper.BuildDescription(TicketActivityType.Created, "Ticket created", null, null),
            Is.EqualTo("Chamado criado"));
    }

    [Test]
    public void BuildDescription_ShouldShowStateTransition()
    {
        Assert.That(
            TicketTimelineMapper.BuildDescription(TicketActivityType.StateChanged, null, "Aberto", "Em andamento"),
            Is.EqualTo("Estado: Aberto → Em andamento"));
    }

    [Test]
    public void BuildDescription_ShouldHandleOnlyNewAndOnlyOld()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                TicketTimelineMapper.BuildDescription(TicketActivityType.Assigned, null, null, "Fulano"),
                Is.EqualTo("Responsável definido: Fulano"));
            Assert.That(
                TicketTimelineMapper.BuildDescription(TicketActivityType.Assigned, null, "Fulano", null),
                Is.EqualTo("Responsável removido (era Fulano)"));
            Assert.That(
                TicketTimelineMapper.BuildDescription(TicketActivityType.Assigned, null, null, null),
                Is.EqualTo("Responsável alterado"));
        });
    }

    [Test]
    public void BuildDescription_ShouldDescribePriorityAndRating()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                TicketTimelineMapper.BuildDescription(TicketActivityType.PriorityChanged, "Priority updated", "Low", "High"),
                Is.EqualTo("Prioridade: Low → High"));
            Assert.That(
                TicketTimelineMapper.BuildDescription(TicketActivityType.Rated, null, null, "5"),
                Is.EqualTo("Chamado avaliado com nota 5"));
        });
    }

    [Test]
    public void BuildDescription_ShouldKeepSpecificCommentForRelations()
    {
        Assert.That(
            TicketTimelineMapper.BuildDescription(
                TicketActivityType.TicketRelationAdded, "Relação RelatesTo criada.", null, "TCK-2"),
            Is.EqualTo("Relação RelatesTo criada."));
    }

    [Test]
    public void ResolveLabel_ShouldPreferResolved_ThenRaw_AndDropNone()
    {
        var map = new Dictionary<string, string> { ["guid-1"] = "Pedro" };

        Assert.Multiple(() =>
        {
            Assert.That(TicketTimelineMapper.ResolveLabel("guid-1", map), Is.EqualTo("Pedro"));
            Assert.That(TicketTimelineMapper.ResolveLabel("guid-2", map), Is.EqualTo("guid-2"));
            Assert.That(TicketTimelineMapper.ResolveLabel("none", map), Is.Null);
            Assert.That(TicketTimelineMapper.ResolveLabel(null, map), Is.Null);
        });
    }

    [Test]
    public void Map_ShouldExposeResolvedLabelsAndAuthor()
    {
        var userId = Guid.NewGuid();
        var log = Log(
            TicketActivityType.Assigned,
            oldValue: "old-user",
            newValue: "new-user",
            changedBy: userId);

        var entry = TicketTimelineMapper.Map(
            log,
            changedByName: "Pedro",
            new Dictionary<string, string> { ["old-user"] = "Ana", ["new-user"] = "Bruno" });

        Assert.Multiple(() =>
        {
            Assert.That(entry.ActivityType, Is.EqualTo(TicketActivityType.Assigned));
            Assert.That(entry.ChangedByName, Is.EqualTo("Pedro"));
            Assert.That(entry.OldLabel, Is.EqualTo("Ana"));
            Assert.That(entry.NewLabel, Is.EqualTo("Bruno"));
            Assert.That(entry.Description, Is.EqualTo("Responsável: Ana → Bruno"));
        });
    }

    [Test]
    public void Map_ShouldNotFailWithoutLabelMap()
    {
        var entry = TicketTimelineMapper.Map(Log(TicketActivityType.Commented, comment: "Comment by x"), null);

        Assert.Multiple(() =>
        {
            Assert.That(entry.Description, Is.EqualTo("Comentário adicionado"));
            Assert.That(entry.ChangedByName, Is.Null);
        });
    }

    [Test]
    public void BuildDescription_ShouldDescribeAiAssignment()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                TicketTimelineMapper.BuildDescription(
                    TicketActivityType.AiAssigned, "Dificuldade 3; origem IA.", null, "Pedro"),
                Is.EqualTo("Atribuído pela triagem por IA: Pedro — Dificuldade 3; origem IA."));
            Assert.That(
                TicketTimelineMapper.BuildDescription(
                    TicketActivityType.AiAssignmentSuggested, null, null, "Ana"),
                Is.EqualTo("Sugestão de responsável pela IA: Ana"));
        });
    }

    [Test]
    public void Map_ShouldCapLongValues()
    {
        var longText = new string('x', TicketTimelineMapper.MaxValueLength + 400);
        var entry = TicketTimelineMapper.Map(
            Log(TicketActivityType.DescriptionUpdated, oldValue: longText, newValue: longText),
            null);

        Assert.Multiple(() =>
        {
            Assert.That(entry.OldValue, Does.EndWith("…"));
            Assert.That(entry.OldValue!.Length, Is.EqualTo(TicketTimelineMapper.MaxValueLength + 1));
            Assert.That(entry.NewValue!.Length, Is.EqualTo(TicketTimelineMapper.MaxValueLength + 1));
        });
    }

    [Test]
    public void Map_ShouldKeepShortValuesUntouched()
    {
        var entry = TicketTimelineMapper.Map(
            Log(TicketActivityType.PriorityChanged, oldValue: "Low", newValue: "High"),
            null);

        Assert.Multiple(() =>
        {
            Assert.That(entry.OldValue, Is.EqualTo("Low"));
            Assert.That(entry.NewValue, Is.EqualTo("High"));
        });
    }

    [Test]
    public void BuildDescription_ShouldNotShowRawGuidsForWorkflowProfileChange()
    {
        Assert.That(
            TicketTimelineMapper.BuildDescription(
                TicketActivityType.StateChanged, "Workflow profile changed", "guid-a", "guid-b"),
            Is.EqualTo("Perfil de workflow alterado"));
    }
}
