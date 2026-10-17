using Discovery.Core.Entities;
using Discovery.Infrastructure.Services.Ai;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Seleção/envio das imagens anexadas ao chamado para a triagem por IA.
/// Cobre as regras puras (sem storage): tipo aceito, soft delete, teto de
/// tamanho, ordem e limite de imagens por prompt.
/// </summary>
[TestFixture]
public class TicketAttachmentImageSourceTests
{
    private static Attachment Image(string contentType, long size, DateTime? createdAt = null, bool deleted = false)
        => new()
        {
            Id = Guid.NewGuid(),
            EntityType = "Ticket",
            EntityId = Guid.NewGuid(),
            FileName = "print.png",
            ContentType = contentType,
            SizeBytes = size,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            DeletedAt = deleted ? DateTime.UtcNow : null
        };

    [Test]
    public void Select_KeepsOnlyAcceptedImageTypes()
    {
        var items = new[]
        {
            Image("image/png", 1024),
            Image("application/pdf", 1024),
            Image("text/plain", 10),
            Image("image/webp", 2048)
        };

        var selected = TicketAttachmentImageSource.SelectImageAttachmentsCore(items);

        Assert.That(selected, Has.Count.EqualTo(2));
        Assert.That(selected.Select(a => a.ContentType), Is.EquivalentTo(new[] { "image/png", "image/webp" }));
    }

    [Test]
    public void Select_IgnoresDeletedFiles()
    {
        var items = new[] { Image("image/png", 1024, deleted: true), Image("image/jpeg", 1024) };

        var selected = TicketAttachmentImageSource.SelectImageAttachmentsCore(items);

        Assert.That(selected, Has.Count.EqualTo(1));
        Assert.That(selected[0].ContentType, Is.EqualTo("image/jpeg"));
    }

    [Test]
    public void Select_RespectsPerFileSizeCap()
    {
        var items = new[]
        {
            Image("image/png", TicketAttachmentImageSource.MaxBytesPerImage + 1),
            Image("image/png", 0),
            Image("image/png", TicketAttachmentImageSource.MaxBytesPerImage)
        };

        var selected = TicketAttachmentImageSource.SelectImageAttachmentsCore(items);

        Assert.That(selected, Has.Count.EqualTo(1));
        Assert.That(selected[0].SizeBytes, Is.EqualTo(TicketAttachmentImageSource.MaxBytesPerImage));
    }

    [Test]
    public void Select_LimitsToMaxImagesNewestFirst()
    {
        var now = DateTime.UtcNow;
        var items = Enumerable.Range(0, 5)
            .Select(i => Image("image/png", 1024, createdAt: now.AddMinutes(-i)))
            .ToArray();

        var selected = TicketAttachmentImageSource.SelectImageAttachmentsCore(items);

        Assert.That(selected, Has.Count.EqualTo(TicketAttachmentImageSource.MaxImagesPerPrompt));
        Assert.That(selected[0].CreatedAt, Is.GreaterThan(selected[^1].CreatedAt));
    }

    [Test]
    public void NormalizeContentType_TrimsParametersAndCase()
    {
        Assert.That(TicketAttachmentImageSource.NormalizeContentType(" Image/PNG ; charset=binary "), Is.EqualTo("image/png"));
        Assert.That(TicketAttachmentImageSource.NormalizeContentType(string.Empty), Is.Empty);
    }

    [Test]
    public void ToDataUrl_ProducesBase64OnlyForAcceptedTypes()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };

        var url = TicketAttachmentImageSource.ToDataUrl(bytes, "image/png; charset=binary");
        Assert.That(url, Is.EqualTo("data:image/png;base64," + Convert.ToBase64String(bytes)));

        Assert.That(TicketAttachmentImageSource.ToDataUrl(bytes, "application/pdf"), Is.Null);
        Assert.That(TicketAttachmentImageSource.ToDataUrl(Array.Empty<byte>(), "image/png"), Is.Null);
    }
}
