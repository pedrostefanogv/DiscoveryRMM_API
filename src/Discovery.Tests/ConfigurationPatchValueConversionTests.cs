using System.Text.Json;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Regressão do PATCH /api/v1/configurations/server: o console envia
/// "attachmentTicketTotalQuotaBytes":0 (propriedade long) e o antigo
/// ConvertJsonElement devolvia GetRawText() (string) para qualquer tipo
/// que não fosse bool/int/string, causando
/// "Object of type 'System.String' cannot be converted to type 'System.Int64'".
/// </summary>
[TestFixture]
public class ConfigurationPatchValueConversionTests
{
    private static object? Convert(object value, Type targetType)
        => ConfigurationService.ConvertToPropertyValue(value, targetType);

    private static JsonElement Json(string raw)
        => JsonSerializer.Deserialize<JsonElement>(raw);

    [TestCase(0)]
    [TestCase(3600)]
    public void JsonNumber_ShouldConvertToLong(long value)
    {
        var result = Convert(Json(value.ToString()), typeof(long));
        Assert.That(result, Is.InstanceOf<long>());
        Assert.That(result, Is.EqualTo(value));
    }

    [Test]
    public void JsonNumber_ShouldConvertToInt()
    {
        var result = Convert(Json("24"), typeof(int));
        Assert.That(result, Is.InstanceOf<int>());
        Assert.That(result, Is.EqualTo(24));
    }

    [Test]
    public void StringNumber_ShouldConvertToLong()
    {
        var result = Convert(Json("\"42\""), typeof(long));
        Assert.That(result, Is.InstanceOf<long>());
        Assert.That(result, Is.EqualTo(42L));
    }

    [Test]
    public void StringNumber_ShouldConvertToInt()
    {
        var result = Convert(Json("\"24\""), typeof(int));
        Assert.That(result, Is.InstanceOf<int>());
        Assert.That(result, Is.EqualTo(24));
    }

    [Test]
    public void JsonTrue_ShouldConvertToBool()
    {
        var result = Convert(Json("true"), typeof(bool));
        Assert.That(result, Is.EqualTo(true));
    }

    [Test]
    public void StringTrue_ShouldConvertToBool()
    {
        var result = Convert(Json("\"true\""), typeof(bool));
        Assert.That(result, Is.EqualTo(true));
    }

    [Test]
    public void Text_ShouldConvertToString()
    {
        var result = Convert(Json("\"some text\""), typeof(string));
        Assert.That(result, Is.EqualTo("some text"));
    }

    [Test]
    public void JsonObject_ShouldConvertToRawJsonString()
    {
        var result = Convert(Json("{\"a\":1}"), typeof(string));
        Assert.That(result, Is.EqualTo("{\"a\":1}"));
    }

    [Test]
    public void Null_ShouldBeNullForNullableLong()
    {
        var result = Convert(Json("null"), typeof(long?));
        Assert.That(result, Is.Null);
    }

    [Test]
    public void Null_ShouldBeZeroForNonNullableLong()
    {
        var result = Convert(Json("null"), typeof(long));
        Assert.That(result, Is.EqualTo(0L));
    }

    [Test]
    public void StringDateTimeOffset_ShouldConvert()
    {
        var result = Convert(Json("\"2026-11-01T23:55:07Z\""), typeof(DateTimeOffset));
        Assert.That(result, Is.InstanceOf<DateTimeOffset>());
        Assert.That(((DateTimeOffset)result!).UtcDateTime, Is.EqualTo(new DateTime(2026, 11, 1, 23, 55, 7, DateTimeKind.Utc)));
    }

    [Test]
    public void StringTimeSpan_ShouldConvert()
    {
        var result = Convert(Json("\"01:30:00\""), typeof(TimeSpan));
        Assert.That(result, Is.EqualTo(TimeSpan.FromHours(1.5)));
    }

    [Test]
    public void InvalidBoolString_ShouldThrowClearArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => Convert(Json("\"sim\""), typeof(bool)));
        Assert.That(ex!.Message, Does.Contain("Boolean"));
    }

    [Test]
    public void JsonNumberForBool_ShouldThrowClearArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => Convert(Json("1"), typeof(bool)));
        Assert.That(ex!.Message, Does.Contain("Boolean"));
    }

    [Test]
    public void InvalidGuid_ShouldThrowClearArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => Convert(Json("\"nao-e-um-guid\""), typeof(Guid)));
        Assert.That(ex!.Message, Does.Contain("Guid"));
    }

    [Test]
    public void JsonZeroToLong_ShouldNotThrowArgumentException()
    {
        // Caso de regressão exato do payload do console:
        // "attachmentTicketTotalQuotaBytes":0 sobre AttachmentTicketTotalQuotaBytes (long).
        Assert.DoesNotThrow(() => Convert(Json("0"), typeof(long)));
        var result = Convert(Json("0"), typeof(long));
        Assert.That(result, Is.EqualTo(0L));
    }
}
