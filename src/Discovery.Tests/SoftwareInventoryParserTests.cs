using System.Reflection;
using Discovery.Api.Controllers;
using Discovery.Core.Entities;

namespace Discovery.Tests;

public class SoftwareInventoryParserTests
{
    [Test]
    public void ToEntry_ShouldParseCompactInstallDateAndInstallSource()
    {
        var item = new SoftwareInventoryItemRequest(
            Name: "Google Chrome",
            Version: "136.0.7103.93",
            Publisher: "Google LLC",
            InstallId: "{8A69D345-D564-463C-AFF1-A69D9E530F96}",
            Serial: "{8A69D345-D564-463C-AFF1-A69D9E530F96}",
            Source: "osquery/programs",
            InstallDate: "20260501",
            InstallSource: @"C:\Program Files\Google\Chrome\Application");

        var entry = InvokeToEntry(item);

        Assert.That(entry.InstallDate, Is.EqualTo(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(entry.InstallSource, Is.EqualTo(@"C:\Program Files\Google\Chrome\Application"));
    }

    [Test]
    public void ToEntry_ShouldKeepInstallMetadataNull_WhenPayloadIsLegacy()
    {
        var item = new SoftwareInventoryItemRequest(
            Name: "Discovery Agent",
            Version: "1.0.0",
            Publisher: "Discovery",
            InstallId: "discovery-agent",
            Serial: "discovery-agent",
            Source: "osquery/programs",
            InstallDate: null,
            InstallSource: null);

        var entry = InvokeToEntry(item);

        Assert.That(entry.InstallDate, Is.Null);
        Assert.That(entry.InstallSource, Is.Null);
    }

    [Test]
    public void ParseInstallDate_ShouldParseIsoAndReturnDateAtUtcMidnight()
    {
        var parsed = InvokeParseInstallDate("2026-05-06T21:40:12Z");

        Assert.That(parsed, Is.EqualTo(new DateTime(2026, 5, 6, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void ParseInstallDate_ShouldReturnNull_WhenValueIsEmptyOrInvalid()
    {
        Assert.That(InvokeParseInstallDate(""), Is.Null);
        Assert.That(InvokeParseInstallDate("   "), Is.Null);
        Assert.That(InvokeParseInstallDate("valor-invalido"), Is.Null);
    }

    private static SoftwareInventoryEntry InvokeToEntry(SoftwareInventoryItemRequest item)
    {
        var parserType = typeof(AgentAuthController).Assembly.GetType("Discovery.Api.Controllers.SoftwareInventoryParser");
        Assert.That(parserType, Is.Not.Null, "Could not find SoftwareInventoryParser type.");

        var method = parserType!.GetMethod("ToEntry", BindingFlags.Public | BindingFlags.Static);
        Assert.That(method, Is.Not.Null, "Could not find ToEntry method.");

        var result = method!.Invoke(null, [item]) as SoftwareInventoryEntry;
        Assert.That(result, Is.Not.Null, "ToEntry returned null.");

        return result!;
    }

    private static DateTime? InvokeParseInstallDate(string? rawValue)
    {
        var parserType = typeof(AgentAuthController).Assembly.GetType("Discovery.Api.Controllers.SoftwareInventoryParser");
        Assert.That(parserType, Is.Not.Null, "Could not find SoftwareInventoryParser type.");

        var method = parserType!.GetMethod("ParseInstallDate", BindingFlags.Public | BindingFlags.Static);
        Assert.That(method, Is.Not.Null, "Could not find ParseInstallDate method.");

        var result = method!.Invoke(null, [rawValue]);
        return result is null ? null : (DateTime?)result;
    }
}
