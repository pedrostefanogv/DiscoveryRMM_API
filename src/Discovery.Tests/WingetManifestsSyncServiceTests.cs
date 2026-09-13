using System.Diagnostics;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Testes do WingetManifestsSyncService com repositório git local (file://):
/// primeira carga completa, no-op quando o upstream não muda (short-circuit),
/// import incremental de pacote novo e auto-cura quando o catálogo está vazio.
/// </summary>
public class WingetManifestsSyncServiceTests
{
    /// <summary>Fake do repositório: conta chamadas de BulkUpsert e simula o total do catálogo.</summary>
    private sealed class FakeAppPackageRepository : IAppPackageRepository
    {
        public List<IReadOnlyList<AppPackage>> BulkCalls { get; } = [];

        /// <summary>Total "no banco" — alimentado por BulkUpsertAsync, ajustável nos testes.</summary>
        public int CatalogCount { get; set; }

        public Task<AppPackage?> GetByInstallationTypeAndPackageIdAsync(
            AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(IReadOnlyList<AppPackage> Items, int TotalCount)> SearchPageAsync(
            AppInstallationType installationType, string? search, string? architecture, string? cursor, int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<(IReadOnlyList<AppPackage> Items, int TotalCount)>(([], CatalogCount));

        public Task<IReadOnlyList<AppPackage>> GetAllByInstallationTypeAsync(
            AppInstallationType installationType, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> BulkUpsertAsync(
            IReadOnlyList<AppPackage> packages, AppInstallationType installationType,
            CancellationToken cancellationToken = default, bool preventDowngrade = false)
        {
            BulkCalls.Add(packages);
            CatalogCount += packages.Count;
            return Task.FromResult(packages.Count);
        }

        public Task<AppPackage> UpsertCustomAsync(AppPackage package, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static string RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        Assert.That(process, Is.Not.Null, "git não encontrado no PATH");
        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        Assert.That(process.WaitForExit(30_000), Is.True, $"git {string.Join(' ', args)} excedeu o timeout");
        var output = (stdoutTask.Result + stderrTask.Result).Trim();
        Assert.That(process.ExitCode, Is.Zero, $"git {string.Join(' ', args)} falhou: {output}");
        return stdoutTask.Result;
    }

    /// <summary>
    /// Estrutura espelha o winget-pkgs: manifests/&lt;letra&gt;/&lt;Publisher&gt;/&lt;Package&gt;/&lt;Version&gt;.
    /// O diretório do pacote usa o último segmento do PackageId (regra do ResolveVersionDir).
    /// </summary>
    private static void WriteManifest(string originPath, string packageId, string version)
    {
        var letter = packageId[..1].ToLowerInvariant();
        var publisherDir = $"{packageId.Split('.')[0]}Pub";
        var packageDir = packageId.Split('.').Last();
        var dir = Path.Combine(originPath, "manifests", letter, publisherDir, packageDir, version);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, $"{packageId}.installer.yaml"),
            $"""
             PackageIdentifier: {packageId}
             PackageVersion: {version}
             Installers:
               - Architecture: x64
                 InstallerUrl: https://example.com/{packageId}-{version}-x64.exe
                 InstallerSha256: aabbcc
                 InstallerType: exe
                 InstallerSwitches:
                   Silent: /S
             """);

        File.WriteAllText(Path.Combine(dir, $"{packageId}.locale.en-US.yaml"),
            $"""
             PackageIdentifier: {packageId}
             PackageName: {packageId} {version}
             Publisher: Test Publisher
             ShortDescription: Pacote de teste {packageId}
             """);
    }

    private static (string OriginPath, string ClonePath) CreateFixture(
        out FakeAppPackageRepository repo, out WingetManifestsSyncService service)
    {
        var root = Path.Combine(Path.GetTempPath(), "winget-sync-tests", Guid.NewGuid().ToString("N"));
        var origin = Path.Combine(root, "origin");
        var clone = Path.Combine(root, "clone");
        Directory.CreateDirectory(origin);

        RunGit(origin, "init", "-b", "master");
        WriteManifest(origin, "One.App", "1.0.0");
        RunGit(origin, "add", "-A");
        RunGit(origin, "-c", "user.email=test@local", "-c", "user.name=test", "commit", "-m", "init");

        repo = new FakeAppPackageRepository();
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppCatalog:Winget:ClonePath"] = clone,
            ["AppCatalog:Winget:RepoUrl"] = new Uri(origin).AbsoluteUri,
            ["AppCatalog:Winget:Branch"] = "master",
            ["AppCatalog:Winget:GitTimeoutSeconds"] = "60",
        }).Build();
        service = new WingetManifestsSyncService(config, repo, new WingetManifestParser(), NullLogger<WingetManifestsSyncService>.Instance);

        return (origin, clone);
    }

    [Test]
    public async Task Sync_FirstRun_ImportsFullCatalog()
    {
        CreateFixture(out var repo, out var service);

        var result = await service.SyncFromManifestsAsync();

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.PackagesUpserted, Is.EqualTo(1));
        Assert.That(repo.BulkCalls.SelectMany(b => b).Select(p => p.PackageId), Does.Contain("One.App"));
    }

    [Test]
    public async Task Sync_UnchangedUpstream_SkipsImport()
    {
        CreateFixture(out var repo, out var service);
        await service.SyncFromManifestsAsync();

        repo.BulkCalls.Clear();

        var result = await service.SyncFromManifestsAsync();

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.PackagesUpserted, Is.Zero, "upstream sem alterações não deve reimportar o catálogo");
        Assert.That(repo.BulkCalls, Is.Empty, "nenhum BulkUpsert deve ocorrer com upstream inalterado");
    }

    [Test]
    public async Task Sync_NewPackageUpstream_ImportsOnlyIncrement()
    {
        var (origin, _) = CreateFixture(out var repo, out var service);
        await service.SyncFromManifestsAsync();
        repo.BulkCalls.Clear();

        WriteManifest(origin, "Two.App", "2.0.0");
        RunGit(origin, "add", "-A");
        RunGit(origin, "-c", "user.email=test@local", "-c", "user.name=test", "commit", "-m", "add Two.App");

        var result = await service.SyncFromManifestsAsync();

        Assert.That(result.Success, Is.True, result.Error);
        var imported = repo.BulkCalls.SelectMany(b => b).Select(p => p.PackageId).ToList();
        Assert.That(imported, Does.Contain("Two.App"), "pacote novo upstream deve ser importado");
        Assert.That(imported, Does.Not.Contain("One.App"), "import incremental não deve tocar pacotes inalterados");
        Assert.That(result.PackagesUpserted, Is.EqualTo(1));
    }

    [Test]
    public async Task Sync_UnchangedUpstreamButEmptyCatalog_ReimportsFullCatalog()
    {
        CreateFixture(out var repo, out var service);
        await service.SyncFromManifestsAsync();

        repo.CatalogCount = 0; // simula catálogo vazio no banco (wipe/perda)
        repo.BulkCalls.Clear();

        var result = await service.SyncFromManifestsAsync();

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.PackagesUpserted, Is.EqualTo(1), "catálogo vazio deve disparar carga completa");
        Assert.That(repo.BulkCalls, Is.Not.Empty);
    }
}
