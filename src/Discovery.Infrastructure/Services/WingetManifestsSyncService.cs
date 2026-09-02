using System.Diagnostics;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Opções de configuração do sync de manifests (AppCatalog:Winget).
/// </summary>
public sealed class WingetManifestsSyncOptions
{
    public const string SectionName = "AppCatalog:Winget";

    /// <summary>"manifests" (default) | "feed" | "both".</summary>
    public string Source { get; set; } = "manifests";
    public int ManifestsPollIntervalMinutes { get; set; } = 60;
    public string ClonePath { get; set; } = "/var/lib/discovery/winget-pkgs";
    public string RepoUrl { get; set; } = "https://github.com/microsoft/winget-pkgs.git";
    public string Branch { get; set; } = "master";
    public int GitTimeoutSeconds { get; set; } = 900;
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Mantém o catálogo Winget (app_packages) fresco a partir de um shallow clone
/// do branch master do microsoft/winget-pkgs.
///
/// Fluxo: garantir clone (--depth 1 --single-branch) → git pull --depth 1 →
/// diff incremental entre pulls (fallback: varredura completa) → parse YAML →
/// BulkUpsertAsync com anti-downgrade. Upsert-only: falha nunca limpa o catálogo.
/// </summary>
public class WingetManifestsSyncService : IWingetManifestsSyncService
{
    private const string ManifestsDirName = "manifests";

    private readonly WingetManifestsSyncOptions _options;
    private readonly IAppPackageRepository _appPackageRepository;
    private readonly WingetManifestParser _parser;
    private readonly ILogger<WingetManifestsSyncService> _logger;

    /// <summary>Serializa execuções (Quartz job + trigger manual podem concorrer).</summary>
    private static readonly SemaphoreSlim SyncGate = new(1, 1);

    public WingetManifestsSyncService(
        IConfiguration configuration,
        IAppPackageRepository appPackageRepository,
        WingetManifestParser parser,
        ILogger<WingetManifestsSyncService> logger)
    {
        _options = configuration.GetSection(WingetManifestsSyncOptions.SectionName).Get<WingetManifestsSyncOptions>()
                   ?? new WingetManifestsSyncOptions();
        _appPackageRepository = appPackageRepository;
        _parser = parser;
        _logger = logger;
    }

    public async Task<AppCatalogSyncResultDto> SyncFromManifestsAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var acquired = false;
        try
        {
            // Gate estático: adquirido com flag para nunca dar Release sem acquire
            // (cancelamento durante o wait não pode corromper o semáforo).
            await SyncGate.WaitAsync(cancellationToken);
            acquired = true;

            var changed = await EnsureCloneAndPullAsync(cancellationToken);
            var commitDate = await GetHeadCommitDateAsync(cancellationToken);

            var versionDirs = await CollectVersionDirectoriesAsync(changed, cancellationToken);
            if (versionDirs.Count == 0)
            {
                stopwatch.Stop();
                _logger.LogInformation("Winget manifests sync: nenhuma alteração a importar.");
                return Ok(0, startedAt, stopwatch, commitDate);
            }

            var upserted = await ImportAsync(versionDirs, commitDate, cancellationToken);

            await RunGitAsync(["gc", "--prune=now"], cancellationToken, logErrorsAsWarning: true);

            stopwatch.Stop();
            _logger.LogInformation(
                "Winget manifests sync concluído: {Dirs} dirs de versão avaliados, {Upserted} pacotes upserted em {Duration}.",
                versionDirs.Count, upserted, stopwatch.Elapsed);

            return Ok(upserted, startedAt, stopwatch, commitDate);
        }
        catch (OperationCanceledException)
        {
            return Fail("Sync was cancelled.", startedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Winget manifests sync falhou (catálogo atual preservado).");
            return Fail(ex.Message, startedAt);
        }
        finally
        {
            if (acquired)
                SyncGate.Release();
        }
    }

    private AppCatalogSyncResultDto Ok(int upserted, DateTime startedAt, Stopwatch stopwatch, DateTime? commitDate) => new()
    {
        InstallationType = AppInstallationType.Winget,
        Success = true,
        PackagesUpserted = upserted,
        PagesProcessed = upserted,
        SyncedAt = startedAt,
        SourceGeneratedAt = commitDate,
        Duration = stopwatch.Elapsed
    };

    private AppCatalogSyncResultDto Fail(string error, DateTime startedAt) => new()
    {
        InstallationType = AppInstallationType.Winget,
        Success = false,
        PackagesUpserted = 0,
        PagesProcessed = 0,
        SyncedAt = startedAt,
        Duration = TimeSpan.Zero,
        Error = error
    };

    // ── Git ──────────────────────────────────────────────────────────────

    /// <summary>Garante o clone raso e faz pull. Retorna true se houve mudança (fast-forward).</summary>
    private async Task<bool> EnsureCloneAndPullAsync(CancellationToken ct)
    {
        var clonePath = _options.ClonePath;
        var tmpPath = clonePath + ".tmp";

        if (!Directory.Exists(Path.Combine(clonePath, ".git")))
        {
            if (Directory.Exists(tmpPath))
            {
                _logger.LogWarning("Clone temporário órfão encontrado em {Tmp}; removendo antes de novo clone.", tmpPath);
                Directory.Delete(tmpPath, recursive: true);
            }

            // Diretório destino pré-existente sem .git (criado manualmente ou clone
            // corrompido): não é um clone válido — remove para permitir o Move atômico.
            if (Directory.Exists(clonePath))
            {
                _logger.LogWarning("{Path} existe sem .git; removendo antes de novo clone.", clonePath);
                Directory.Delete(clonePath, recursive: true);
            }

            var parent = Path.GetDirectoryName(clonePath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            _logger.LogInformation("Clonando winget-pkgs (shallow) para {Path}...", clonePath);
            await RunGitAsync(["clone", "--depth", "1", "--single-branch", "--branch", _options.Branch, _options.RepoUrl, tmpPath], ct);
            Directory.Move(tmpPath, clonePath);
            _logger.LogInformation("Clone inicial do winget-pkgs concluído.");

            return true; // primeira carga: import completo
        }

        var (ok, output) = await TryRunGitAsync(["pull", "--depth", "1", "--ff-only"], ct);
        if (ok)
        {
            var upToDate = output.Contains("Already up to date", StringComparison.OrdinalIgnoreCase)
                           || output.Contains("Já está atualizado", StringComparison.OrdinalIgnoreCase);
            return !upToDate;
        }

        // Shallow pull pode falhar por falta de ref-history; força reset ao origin.
        _logger.LogWarning("git pull falhou ({Error}); tentando fetch --depth 1 + reset --hard.", Truncate(output));
        await RunGitAsync(["fetch", "--depth", "1", "origin", _options.Branch], ct);
        var (resetOk, resetOut) = await TryRunGitAsync(["reset", "--hard", $"origin/{_options.Branch}"], ct);
        if (!resetOk)
            throw new InvalidOperationException($"git reset falhou: {Truncate(resetOut)}");

        return true;
    }

    private async Task<DateTime?> GetHeadCommitDateAsync(CancellationToken ct)
    {
        var (ok, output) = await TryRunGitAsync(["log", "-1", "--format=%cI"], ct);
        return ok && DateTime.TryParse(output.Trim(), out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }

    // ── Coleta de manifests ──────────────────────────────────────────────

    /// <summary>
    /// Determina os diretórios de versão a importar: incremental (diff entre pulls)
    /// ou varredura completa. Cada diretório de versão vira no máx. 1 candidato.
    /// </summary>
    private async Task<List<(string PackageId, string Version, string Dir)>> CollectVersionDirectoriesAsync(bool changed, CancellationToken ct)
    {
        var manifestsRoot = Path.Combine(_options.ClonePath, ManifestsDirName);
        var result = new List<(string, string, string)>();

        if (!Directory.Exists(manifestsRoot))
        {
            _logger.LogError("Diretório de manifests não encontrado em {Root}.", manifestsRoot);
            return result;
        }

        if (changed && await TryGetChangedPackageVersionsAsync(ct) is { Count: > 0 } incremental)
        {
            foreach (var (packageId, version) in incremental)
            {
                var dir = ResolveVersionDir(manifestsRoot, packageId, version);
                if (dir is not null)
                    result.Add((packageId, version, dir));
            }

            if (result.Count > 0)
                return result;

            _logger.LogInformation("Diff incremental não resolveu nenhum diretório válido; executando varredura completa.");
        }

        // Varredura completa: UMA passada pelos arquivos *.installer.yaml (muito mais
        // barato que enumerar todos os diretórios). O PackageId vem do NOME DO ARQUIVO
        // (<PackageId>.installer.yaml) — fonte confiável (o nome do diretório do
        // pacote pode divergir, ex. id "0-don.clippy" → dir "clippy").
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var installerFile in Directory.EnumerateFiles(manifestsRoot, "*.installer.yaml", SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(installerFile)!;
            if (!seenDirs.Add(dir))
                continue; // múltiplos installer.yaml no mesmo dir → importa 1x

            var fileName = Path.GetFileName(installerFile);
            var version = Path.GetFileName(dir);
            var packageId = fileName.EndsWith(".installer.yaml", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^".installer.yaml".Length]
                : null;

            if (string.IsNullOrEmpty(packageId) || string.IsNullOrEmpty(version))
                continue;

            result.Add((packageId, version, dir));
        }

        // Tolerância ao nome genérico Installer.yaml (não existe no repo oficial hoje).
        foreach (var installerFile in Directory.EnumerateFiles(manifestsRoot, "Installer.yaml", SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(installerFile)!;
            if (!seenDirs.Add(dir))
                continue;

            var version = Path.GetFileName(dir);
            var package = Path.GetFileName(Path.GetDirectoryName(dir));
            if (string.IsNullOrEmpty(package) || package.StartsWith('.'))
                continue;

            result.Add((package, version, dir));
        }

        return result;
    }

    /// <summary>Tenta derivar (PackageId, Version) alterados via git diff entre o pull anterior e o HEAD.</summary>
    private async Task<List<(string PackageId, string Version)>?> TryGetChangedPackageVersionsAsync(CancellationToken ct)
    {
        var (ok, output) = await TryRunGitAsync(["diff", "--name-only", "HEAD@{1}", "HEAD", "--", ManifestsDirName], ct);
        if (!ok)
            return null;

        var changed = new List<(string PackageId, string Version)>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // manifests/<letra>/<Publisher>/<Package>/<Version>/<PackageId>.installer.yaml
            // O PackageId vem do NOME DO ARQUIVO — o nome do diretório do pacote pode
            // divergir (ex. id "0-don.clippy" → dir "clippy"), o que quebraria o
            // first-letter do ResolveVersionDir.
            var parts = line.Split('/');
            if (parts.Length < 5 || !parts[0].Equals(ManifestsDirName, StringComparison.OrdinalIgnoreCase))
                continue;

            var version = parts[^2];
            var fileName = parts[^1];
            var packageId = ParsePackageIdFromFileName(fileName) ?? parts[^3];

            if (!string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(packageId))
                changed.Add((packageId, version));
        }

        return changed;
    }

    /// <summary>
    /// Extrai o PackageId do nome de um manifest:
    /// "0-don.clippy.installer.yaml" → "0-don.clippy";
    /// "Foxit.FoxitReader.locale.en-US.yaml" → "Foxit.FoxitReader";
    /// "Installer.yaml"/"DefaultLocale.yaml" (genéricos) → null (usa o dir).
    /// </summary>
    private static string? ParsePackageIdFromFileName(string fileName)
    {
        if (!fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            return null;

        var withoutExt = fileName[..^".yaml".Length];

        if (withoutExt.EndsWith(".installer", StringComparison.OrdinalIgnoreCase))
            return withoutExt[..^".installer".Length];

        var localeIdx = withoutExt.IndexOf(".locale.", StringComparison.OrdinalIgnoreCase);
        if (localeIdx > 0)
            return withoutExt[..localeIdx];

        if (withoutExt.Equals("Installer", StringComparison.OrdinalIgnoreCase)
            || withoutExt.Equals("DefaultLocale", StringComparison.OrdinalIgnoreCase))
            return null;

        // <PackageId>.yaml (manifest version) — só se parece um ID (contém ponto).
        return withoutExt.Contains('.') ? withoutExt : null;
    }

    private string? ResolveVersionDir(string manifestsRoot, string packageId, string version)
    {
        // O path real usa o nome do diretório do publisher no repo, que pode diferir
        // do primeiro segmento do PackageId (ex. id "0-don.clippy" → dirs 0/0-don/clippy).
        // Busca: manifests/<letra>/<publisher-dir>/<package-dir>/<version> via glob.
        var first = packageId[..1].ToLowerInvariant();
        var letterRoot = Path.Combine(manifestsRoot, first);
        if (!Directory.Exists(letterRoot))
            return null;

        var versionDirName = version;
        var packageDirName = packageId.Split('.').Last();

        foreach (var publisherDir in Directory.EnumerateDirectories(letterRoot))
        {
            var candidate = Path.Combine(publisherDir, packageDirName, versionDirName);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    // ── Import ───────────────────────────────────────────────────────────

    private async Task<int> ImportAsync(List<(string PackageId, string Version, string Dir)> versionDirs, DateTime? commitDate, CancellationToken ct)
    {
        // Uma linha por PackageId: agrupa TODAS as versões avaliadas, ordenadas da
        // maior para a menor. Se o parse da maior falhar (manifest malformado),
        // tenta a próxima — o pacote não pode sumir do catálogo por um YAML quebrado.
        var versionsPerPackage = new Dictionary<string, List<(string Version, string Dir)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (packageId, version, dir) in versionDirs)
        {
            if (!versionsPerPackage.TryGetValue(packageId, out var list))
            {
                list = [];
                versionsPerPackage[packageId] = list;
            }

            list.Add((version, dir));
        }

        var packages = new List<AppPackage>();
        var parseFailures = 0;

        foreach (var (packageId, versions) in versionsPerPackage)
        {
            ct.ThrowIfCancellationRequested();

            AppPackage? parsed = null;

            foreach (var (version, dir) in versions
                         .OrderByDescending(v => v.Version, WingetVersionComparer.Default))
            {
                parsed = _parser.Parse(packageId, version, dir, commitDate);
                if (parsed is not null)
                    break;

                parseFailures++;
            }

            if (parsed is null)
                continue;

            packages.Add(parsed);
        }

        if (parseFailures > 0)
            _logger.LogWarning("Winget manifests sync: {Count} manifests malformados ignorados.", parseFailures);

        var upserted = 0;
        foreach (var batch in packages.Chunk(200))
        {
            upserted += await _appPackageRepository.BulkUpsertAsync(
                batch,
                AppInstallationType.Winget,
                ct,
                preventDowngrade: true);
        }

        return upserted;
    }

    // ── Execução de git ──────────────────────────────────────────────────

    private async Task RunGitAsync(string[] args, CancellationToken ct, bool logErrorsAsWarning = false)
    {
        var (ok, output) = await TryRunGitAsync(args, ct);
        if (!ok)
        {
            if (logErrorsAsWarning)
                _logger.LogWarning("git {Args} falhou (ignorado): {Output}", string.Join(' ', args), Truncate(output));
            else
                throw new InvalidOperationException($"git {string.Join(' ', args)} falhou: {Truncate(output)}");
        }
    }

    private async Task<(bool Ok, string Output)> TryRunGitAsync(string[] args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(_options.ClonePath) ? _options.ClonePath : null
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("git não encontrado no servidor.");

        // ReadToEnd sem ct: se o processo morre, os pipes fecham e as tasks completam.
        // Com ct, uma leitura pendente poderia bloquear para sempre após o kill.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var timeoutMs = _options.GitTimeoutSeconds * 1000;
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var delayTask = Task.Delay(timeoutMs, CancellationToken.None);
        var completed = exitTask == await Task.WhenAny(exitTask, delayTask);

        if (!completed)
        {
            // Timeout OU cancelamento externo: mata a árvore inteira para não deixar git órfão.
            try { process.Kill(entireProcessTree: true); } catch { /* já encerrado */ }

            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            throw new TimeoutException($"git {string.Join(' ', args)} excedeu {_options.GitTimeoutSeconds}s.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var ok = process.ExitCode == 0;
        return (ok, ok ? stdout : $"{stdout}{Environment.NewLine}{stderr}".Trim());
    }

    private static string Truncate(string value, int max = 500) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max] + "...";
}
