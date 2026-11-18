using System.Text.Json;
using System.Xml.Linq;
using Discovery.Migrations.Migrations;

namespace Discovery.E2E.Tests.Tier2;

[TestFixture]
public class F10_NuGetAndBuildEdgeTests
{
    private static string FindApiRepoRoot()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Discovery.slnx")))
                return dir.FullName;
            var sub = Path.Combine(dir.FullName, "DiscoveryRMM_API");
            if (File.Exists(Path.Combine(sub, "Discovery.slnx")))
                return sub;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate DiscoveryRMM_API root.");
    }

    [Test]
    public void T2_F10_01_SystemFormatsCbor_Stays_At_Safe_Patched_Version()
    {
        var infraPath = Path.Combine(FindApiRepoRoot(), "src", "Discovery.Infrastructure", "Discovery.Infrastructure.csproj");
        var doc = XDocument.Load(infraPath);
        var cbor = doc.Descendants("PackageReference")
            .FirstOrDefault(e => string.Equals(e.Attribute("Include")?.Value, "System.Formats.Cbor", StringComparison.OrdinalIgnoreCase));

        Assert.That(cbor, Is.Not.Null);
        var version = Version.Parse(cbor!.Attribute("Version")!.Value);
        Assert.That(version, Is.GreaterThanOrEqualTo(new Version(10, 0, 15)));
    }

    [Test]
    public void T2_F10_02_DiscoveryTests_Assets_Do_Not_Reference_Vulnerable_SQLitePCLRaw_2_1_11()
    {
        var assetsPath = Path.Combine(FindApiRepoRoot(), "src", "Discovery.Tests", "obj", "project.assets.json");
        Assert.That(File.Exists(assetsPath), Is.True, "Run restore/build on Discovery.Tests first.");

        var text = File.ReadAllText(assetsPath);
        Assert.That(text, Does.Not.Contain("SQLitePCLRaw.lib.e_sqlite3/2.1.11"),
            "Discovery.Tests should update EF.Sqlite so vulnerable SQLitePCLRaw.lib.e_sqlite3 2.1.11 is not resolved.");
    }

    [Test]
    public void T2_F10_03_No_Project_Uses_Floating_Wildcard_Package_Versions()
    {
        var csprojs = Directory.GetFiles(Path.Combine(FindApiRepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories);
        foreach (var path in csprojs)
        {
            var doc = XDocument.Load(path);
            foreach (var pkg in doc.Descendants("PackageReference"))
            {
                var ver = pkg.Attribute("Version")?.Value ?? "";
                Assert.That(ver, Does.Not.Contain("*"), $"Floating NuGet version found in {path}: {pkg}");
            }
        }
    }

    [Test]
    public void T2_F10_04_AppSettings_Json_Files_Have_No_Duplicate_Root_Keys()
    {
        var apiDir = Path.Combine(FindApiRepoRoot(), "src", "Discovery.Api");
        foreach (var file in Directory.GetFiles(apiDir, "appsettings*.json"))
        {
            var bytes = File.ReadAllBytes(file);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            var rootKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                {
                    var key = reader.GetString()!;
                    Assert.That(rootKeys.Add(key), Is.True, $"Duplicate root JSON key '{key}' in {Path.GetFileName(file)}");
                }
            }
        }
    }

    [Test]
    public void T2_F10_05_Migrations_Assembly_Loads_Initial_Migration_Metadata()
    {
        var m001 = typeof(M001_CreateClients);
        Assert.That(m001.Namespace, Is.EqualTo("Discovery.Migrations.Migrations"));
        Assert.That(m001.GetCustomAttributes(inherit: false), Is.Not.Empty);
    }
}
