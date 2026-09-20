using Discovery.Core.Helpers;

namespace Discovery.Tests;

/// <summary>
/// Contrato dos cursores Type B (Guid) e Type A (CreatedAt + Guid):
/// emitir → decodificar deve ser idempotente, e cursores crus legados
/// (formato "N"/"D", sem Base64) continuam decodificáveis via fallback.
/// Regressão do bug do detalhe do agente: GetAgentSoftwareQueryHandler
/// emitia <c>InventoryId.ToString()</c> e o decoder (Base64) rejeitava,
/// repetindo sempre a 1ª página e causando loop infinito de requests.
/// </summary>
public class CursorPaginationHelperTests
{
    private static readonly Guid Id = Guid.Parse("019faa6f-8eaa-7c38-9878-36cc96f9be3e");

    // ------------------------------------------------------------------ Type B

    [Test]
    public void EncodeGuidCursor_RoundTrip_DecodesToSameId()
    {
        var encoded = CursorPaginationHelper.EncodeGuidCursor(Id);

        Assert.That(CursorPaginationHelper.TryDecodeGuidCursor(encoded, out var decoded), Is.True);
        Assert.That(decoded, Is.EqualTo(Id));
    }

    [Test]
    public void TryDecodeGuidCursor_RawLegacyCursorWithHyphens_IsAcceptedByFallback()
    {
        // Formato cru legado emitido por versões anteriores (Guid "D").
        var raw = Id.ToString();

        Assert.That(CursorPaginationHelper.TryDecodeGuidCursor(raw, out var decoded), Is.True);
        Assert.That(decoded, Is.EqualTo(Id));
    }

    [Test]
    public void TryDecodeGuidCursor_RawLegacyCursorNoHyphens_IsAcceptedByFallback()
    {
        var raw = Id.ToString("N");

        Assert.That(CursorPaginationHelper.TryDecodeGuidCursor(raw, out var decoded), Is.True);
        Assert.That(decoded, Is.EqualTo(Id));
    }

    [Test]
    public void TryDecodeGuidCursor_Garbage_ReturnsFalse()
    {
        Assert.That(CursorPaginationHelper.TryDecodeGuidCursor("!!!nao-base64&&&", out _), Is.False);
        Assert.That(CursorPaginationHelper.TryDecodeGuidCursor(null, out _), Is.False);
        Assert.That(CursorPaginationHelper.TryDecodeGuidCursor("", out _), Is.False);
        Assert.That(CursorPaginationHelper.TryDecodeGuidCursor("   ", out _), Is.False);
    }

    [Test]
    public void TryDecodeGuidCursor_NonGuidBase64_ReturnsFalse()
    {
        var notAGuid = Convert.ToBase64String("isto não é um guid"u8.ToArray());

        Assert.That(CursorPaginationHelper.TryDecodeGuidCursor(notAGuid, out _), Is.False);
    }

    // ------------------------------------------------------------------ Type A

    [Test]
    public void EncodeCreatedAtCursor_RoundTrip_DecodesToSameValues()
    {
        var createdAt = new DateTime(2026, 2, 5, 12, 34, 56, DateTimeKind.Utc);

        var encoded = CursorPaginationHelper.EncodeCreatedAtCursor(createdAt, Id);

        Assert.That(
            CursorPaginationHelper.TryDecodeCreatedAtCursor(encoded, out var decodedAt, out var decodedId),
            Is.True);
        Assert.That(decodedAt, Is.EqualTo(createdAt));
        Assert.That(decodedId, Is.EqualTo(Id));
    }
}
