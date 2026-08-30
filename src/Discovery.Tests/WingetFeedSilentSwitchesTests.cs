using System.Text.Json;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Testes do parse de switches silenciosos do feed Winget (installerSwitches por arquitetura).
/// </summary>
public class WingetFeedSilentSwitchesTests
{
    private static (string Silent, string SilentWithProgress) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);

        // ParseSilentSwitches é private static — invoca via reflexão.
        var method = typeof(WingetFeedClient).GetMethod("ParseSilentSwitches",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.That(method, Is.Not.Null, "ParseSilentSwitches não encontrado.");

        var result = method!.Invoke(null, [doc.RootElement]);
        Assert.That(result, Is.Not.Null);

        var silent = (string?)result!.GetType().GetProperty("Silent")?.GetValue(result);
        var silentWithProgress = (string?)result.GetType().GetProperty("SilentWithProgress")?.GetValue(result);
        return (silent ?? string.Empty, silentWithProgress ?? string.Empty);
    }

    [Test]
    public void Parse_InstallerSwitchesPresent_ReturnsSilentAndSilentWithProgress()
    {
        var json = """
        {
            "installerSwitches": {
                "x86": { "Silent": "/S", "SilentWithProgress": "/S" }
            }
        }
        """;

        var (silent, silentWithProgress) = Parse(json);

        Assert.Multiple(() =>
        {
            Assert.That(silent, Is.EqualTo("/S"));
            Assert.That(silentWithProgress, Is.EqualTo("/S"));
        });
    }

    [Test]
    public void Parse_PrefersX64_OverOtherArchs()
    {
        var json = """
        {
            "installerSwitches": {
                "x86": { "Silent": "/S-x86", "SilentWithProgress": "/S-x86" },
                "x64": { "Silent": "/S-x64 /PreventRebootRequired=true", "SilentWithProgress": "/S-x64" }
            }
        }
        """;

        var (silent, _) = Parse(json);

        Assert.That(silent, Is.EqualTo("/S-x64 /PreventRebootRequired=true"));
    }

    [Test]
    public void Parse_CaseInsensitiveKeys()
    {
        var json = """
        {
            "INSTALLERSWITCHES": {
                "X64": { "silent": "/qn", "SILENTWITHPROGRESS": "/qb" }
            }
        }
        """;

        var (silent, silentWithProgress) = Parse(json);

        Assert.Multiple(() =>
        {
            Assert.That(silent, Is.EqualTo("/qn"));
            Assert.That(silentWithProgress, Is.EqualTo("/qb"));
        });
    }

    [Test]
    public void Parse_MissingSilent_FallsBackToSilentWithProgress()
    {
        var json = """
        {
            "installerSwitches": {
                "x64": { "SilentWithProgress": "/S" }
            }
        }
        """;

        var (silent, silentWithProgress) = Parse(json);

        Assert.Multiple(() =>
        {
            Assert.That(silent, Is.Empty);
            Assert.That(silentWithProgress, Is.EqualTo("/S"));
        });
    }

    [Test]
    public void Parse_MissingInstallerSwitches_ReturnsEmpty()
    {
        var json = """{ "id": "7zip.7zip" }""";

        var (silent, silentWithProgress) = Parse(json);

        Assert.Multiple(() =>
        {
            Assert.That(silent, Is.Empty);
            Assert.That(silentWithProgress, Is.Empty);
        });
    }

    [Test]
    public void Parse_NeutralArch_UsedWhenNoOtherArch()
    {
        var json = """
        {
            "installerSwitches": {
                "neutral": { "Silent": "/VERYSILENT /NORESTART", "SilentWithProgress": "/SILENT" }
            }
        }
        """;

        var (silent, _) = Parse(json);

        Assert.That(silent, Is.EqualTo("/VERYSILENT /NORESTART"));
    }
}
