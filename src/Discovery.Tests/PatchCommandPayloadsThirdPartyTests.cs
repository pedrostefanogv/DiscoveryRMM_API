using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Cqrs.Patches;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class PatchCommandPayloadsThirdPartyTests
{
    private static AgentWindowsUpdate Update(string updateKey, string? source) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        UpdateKey = updateKey,
        Title = updateKey,
        Source = source
    };

    [Test]
    public void InstallThirdPartyBuildsAgentContract()
    {
        var payload = PatchCommandPayloads.InstallThirdParty(
            "choco",
            [Update("choco:googlechrome", "choco"), Update("choco:7zip", "choco")]);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        Assert.That(root.GetProperty("action").GetString(), Is.EqualTo("install-third-party"));
        Assert.That(root.GetProperty("source").GetString(), Is.EqualTo("choco"));
        Assert.That(root.GetProperty("ttlSeconds").GetInt32(), Is.EqualTo(1800));
        Assert.That(root.TryGetProperty("issuedAtUtc", out _), Is.True);

        var apps = root.GetProperty("apps");
        Assert.That(apps.GetArrayLength(), Is.EqualTo(2));
        Assert.That(apps[0].GetProperty("id").GetString(), Is.EqualTo("googlechrome"));
        Assert.That(apps[1].GetProperty("id").GetString(), Is.EqualTo("7zip"));
    }

    [Test]
    public void InstallThirdPartyIncludesCatalogIntegrityWhenCurated()
    {
        var catalog = new Dictionary<string, AppCatalogEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["googlechrome"] = new()
            {
                Id = Guid.NewGuid(),
                Source = "choco",
                PackageId = "googlechrome",
                Name = "Google Chrome",
                Version = "121.0",
                InstallerUrl = "https://dl.google.com/chrome/121.msi",
                Sha256 = new string('a', 64),
                InstallerType = "msi",
                SilentArgs = "/qn",
                Status = "active"
            }
        };

        var payload = PatchCommandPayloads.InstallThirdParty(
            "choco",
            [Update("choco:googlechrome", "choco"), Update("choco:7zip", "choco")],
            catalogByAppId: catalog);

        using var document = JsonDocument.Parse(payload);
        var apps = document.RootElement.GetProperty("apps");

        var chrome = apps[0];
        Assert.That(chrome.GetProperty("id").GetString(), Is.EqualTo("googlechrome"));
        Assert.That(chrome.GetProperty("installerUrl").GetString(), Is.EqualTo("https://dl.google.com/chrome/121.msi"));
        Assert.That(chrome.GetProperty("sha256").GetString(), Has.Length.EqualTo(64));
        Assert.That(chrome.GetProperty("silentArgs").GetString(), Is.EqualTo("/qn"));
        Assert.That(chrome.GetProperty("installerType").GetString(), Is.EqualTo("msi"));

        var sevenZip = apps[1];
        Assert.That(sevenZip.TryGetProperty("sha256", out _), Is.False,
            "app sem curadoria não leva metadados de integridade");
    }

    [Test]
    public void SplitBySourceSeparatesOsAndThirdParty()
    {
        var (os, thirdParty) = PatchCommandPayloads.SplitBySource(
        [
            Update("guid:11111111-1111-1111-1111-111111111111:1", "wua"),
            Update("kb:KB5034441", "qfe"),
            Update("choco:googlechrome", "choco"),
            Update("winget:Google.Chrome", "winget"),
            Update("sem-fonte", null)
        ]);

        Assert.That(os, Has.Count.EqualTo(3), "wua/qfe/sem fonte são OS");
        Assert.That(thirdParty, Has.Count.EqualTo(2));
        Assert.That(thirdParty.Select(update => update.UpdateKey),
            Is.EquivalentTo(new[] { "choco:googlechrome", "winget:Google.Chrome" }));
    }

    [TestCase("choco:googlechrome", "googlechrome")]
    [TestCase("winget:Google.Chrome", "Google.Chrome")]
    [TestCase("kb:KB5034441", "KB5034441")]
    [TestCase("semseparador", "semseparador")]
    public void ExtractAppId(string updateKey, string expected)
    {
        Assert.That(PatchCommandPayloads.ExtractAppId(updateKey), Is.EqualTo(expected));
    }
}
