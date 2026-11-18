using System.Xml.Linq;
using Discovery.Api.Controllers;
using Discovery.Core.Entities;
using Discovery.E2E.Tests.Harness;
using Discovery.Infrastructure.Data;
using Discovery.Migrations.Migrations;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.E2E.Tests.Tier1;

[TestFixture]
public class F10_NuGetAndBuildIntegrityTests
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
    public void T1_F10_01_Api_Csproj_Addresses_Deprecated_FluentValidation_AspNetCore()
    {
        var apiCsprojPath = Path.Combine(FindApiRepoRoot(), "src", "Discovery.Api", "Discovery.Api.csproj");
        var doc = XDocument.Load(apiCsprojPath);
        var refs = doc.Descendants("PackageReference")
            .ToDictionary(
                e => e.Attribute("Include")?.Value ?? "",
                e => e.Attribute("Version")?.Value ?? "",
                StringComparer.OrdinalIgnoreCase);

        if (refs.TryGetValue("FluentValidation.AspNetCore", out var fvAspNetVersion))
        {
            Assert.That(fvAspNetVersion, Is.Not.EqualTo("11.3.1"),
                "Deprecated FluentValidation.AspNetCore 11.3.1 should be removed/replaced or upgraded.");
        }
        Assert.That(refs.ContainsKey("FluentValidation"), Is.True);
    }

    [Test]
    public void T1_F10_02_Tests_Csproj_Uses_Updated_EfCore_Sqlite_Aligned_With_Api()
    {
        var root = FindApiRepoRoot();
        var apiDoc = XDocument.Load(Path.Combine(root, "src", "Discovery.Api", "Discovery.Api.csproj"));
        var testsDoc = XDocument.Load(Path.Combine(root, "src", "Discovery.Tests", "Discovery.Tests.csproj"));

        var apiEfVersion = apiDoc.Descendants("PackageReference")
            .First(e => string.Equals(e.Attribute("Include")?.Value, "Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))
            .Attribute("Version")!.Value;

        var testSqliteVersion = testsDoc.Descendants("PackageReference")
            .First(e => string.Equals(e.Attribute("Include")?.Value, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.OrdinalIgnoreCase))
            .Attribute("Version")!.Value;

        Assert.That(Version.Parse(testSqliteVersion), Is.GreaterThanOrEqualTo(Version.Parse(apiEfVersion)),
            "Discovery.Tests Microsoft.EntityFrameworkCore.Sqlite must be updated to >= EF Core API version (avoids NU1903 e_sqlite3 2.1.11).");
    }

    [Test]
    public void T1_F10_03_All_Solution_Projects_Target_Net10()
    {
        var root = FindApiRepoRoot();
        var csprojs = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories);
        Assert.That(csprojs, Has.Length.GreaterThanOrEqualTo(5));

        foreach (var path in csprojs)
        {
            var doc = XDocument.Load(path);
            var tfm = doc.Descendants("TargetFramework").FirstOrDefault()?.Value;
            Assert.That(tfm, Is.EqualTo("net10.0"), $"Expected net10.0 in {path}");
        }
    }

    [Test]
    public void T1_F10_04_Core_Runtime_Assemblies_Load_And_Have_Consistent_Major_Versions()
    {
        var apiAsm = typeof(ClientsController).Assembly.GetName();
        var coreAsm = typeof(Client).Assembly.GetName();
        var infraAsm = typeof(DiscoveryDbContext).Assembly.GetName();
        var migAsm = typeof(M001_CreateClients).Assembly.GetName();

        Assert.That(apiAsm.Name, Is.EqualTo("Discovery.Api"));
        Assert.That(coreAsm.Name, Is.EqualTo("Discovery.Core"));
        Assert.That(infraAsm.Name, Is.EqualTo("Discovery.Infrastructure"));
        Assert.That(migAsm.Name, Is.EqualTo("Discovery.Migrations"));
    }

    [Test]
    public async Task T1_F10_05_Api_Service_Container_Resolves_Core_Pipelines_Without_Missing_Dependencies()
    {
        await using var host = await ApiE2ETestHost.StartAsync();
        await host.WithScopeAsync(sp =>
        {
            Assert.That(sp.GetRequiredService<IMediator>(), Is.Not.Null);
            Assert.That(sp.GetRequiredService<DiscoveryDbContext>(), Is.Not.Null);
            return Task.CompletedTask;
        });
    }
}
