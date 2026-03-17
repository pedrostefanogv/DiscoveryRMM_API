using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Meduza.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Meduza.Infrastructure.Services;

public class AgentPackageService : IAgentPackageService
{
    private readonly IConfiguration _config;
    private static readonly SemaphoreSlim BuildLock = new(1, 1);

    public AgentPackageService(IConfiguration config)
    {
        _config = config;
    }

    public async Task PrebuildBaseBinaryAsync(bool forceRebuild = false, CancellationToken cancellationToken = default)
    {
        var projectPath = _config["AgentPackage:DiscoveryProjectPath"] ?? @"C:\Projetos\Discovery";
        if (!Directory.Exists(projectPath))
            throw new InvalidOperationException($"Discovery project path does not exist: {projectPath}");

        var binaryPath = GetBinaryPath();
        if (!forceRebuild && File.Exists(binaryPath))
            return;

        var wailsPath = ResolveWailsPath();

        await BuildLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check after waiting for lock.
            if (!forceRebuild && File.Exists(binaryPath))
                return;

            // Clean previous binary so stale artifacts don't survive a failed build.
            if (forceRebuild && File.Exists(binaryPath))
                File.Delete(binaryPath);

            await RunProcessAsync(
                fileName: wailsPath,
                workingDirectory: projectPath,
                arguments: ["build", "-s", "-nopackage", "-platform", "windows/amd64"],
                cancellationToken: cancellationToken);

            if (!File.Exists(binaryPath))
                throw new FileNotFoundException("Prebuild finished but binary was not found.", binaryPath);
        }
        finally
        {
            BuildLock.Release();
        }
    }

    public Task<byte[]> BuildPortablePackageAsync(string rawDeployToken)
    {
        var binaryPath = GetBinaryPath();

        var apiScheme = _config["AgentPackage:PublicApiScheme"] ?? "http";

        var apiServer = _config["AgentPackage:PublicApiServer"]
            ?? throw new InvalidOperationException("AgentPackage:PublicApiServer is not configured.");

        var natsUrl = _config["AgentPackage:NatsUrl"] ?? string.Empty;

        if (!File.Exists(binaryPath))
            throw new FileNotFoundException($"Agent binary not found at path: {binaryPath}", binaryPath);

        // Build debug_config.json with deploy token pre-filled.
        // agentId and authToken are intentionally empty so the agent self-registers.
        var debugConfig = new
        {
            apiScheme,
            apiServer,
            natsServer = natsUrl,
            deployToken = rawDeployToken,
            agentId = string.Empty,
            authToken = string.Empty,
            scheme = string.Empty,
            server = string.Empty
        };

        var debugConfigJson = JsonSerializer.Serialize(debugConfig, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Agent binary — store without compression (already a compiled binary)
            var exeEntry = archive.CreateEntry("meduza-discovery.exe", CompressionLevel.NoCompression);
            using (var entryStream = exeEntry.Open())
            using (var fs = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.CopyTo(entryStream);
            }

            // Pre-configured settings file
            var configEntry = archive.CreateEntry("debug_config.json", CompressionLevel.Optimal);
            using (var entryStream = configEntry.Open())
            using (var writer = new StreamWriter(entryStream))
            {
                writer.Write(debugConfigJson);
            }
        }

        return Task.FromResult(ms.ToArray());
    }

    public async Task<(byte[] Content, string FileName)> BuildInstallerAsync(string rawDeployToken)
    {
        var projectPath = _config["AgentPackage:DiscoveryProjectPath"] ?? @"C:\Projetos\Discovery";
        if (!Directory.Exists(projectPath))
            throw new InvalidOperationException($"Discovery project path does not exist: {projectPath}");

        var installerDir = Path.Combine(projectPath, "build", "windows", "installer");
        var projectNsi = Path.Combine(installerDir, "project.nsi");
        if (!File.Exists(projectNsi))
            throw new FileNotFoundException("NSIS project file not found.", projectNsi);

        await PrebuildBaseBinaryAsync();

        var makensisPath = ResolveMakensisPath();

        var publicApiServer = _config["AgentPackage:PublicApiServer"]
            ?? throw new InvalidOperationException("AgentPackage:PublicApiServer is not configured.");

        var defaultDiscovery = (_config["AgentPackage:InstallerDefaults:DiscoveryEnabled"] ?? "1") == "0" ? "0" : "1";
        var defaultMinimal = (_config["AgentPackage:InstallerDefaults:MinimalDefault"] ?? "1") == "0" ? "0" : "1";

        // Deriva o caminho relativo do binário a partir do BinaryPath configurado,
        // garantindo que o nome do arquivo coincida com o que o Wails gerou.
        var binaryRelPath = Path.GetRelativePath(installerDir, GetBinaryPath());
        if (!OperatingSystem.IsWindows())
            binaryRelPath = binaryRelPath.Replace('\\', '/');

        await RunProcessAsync(
            fileName: makensisPath,
            workingDirectory: installerDir,
            arguments:
            [
                $"-DARG_WAILS_AMD64_BINARY={binaryRelPath}",
                $"-DARG_DEFAULT_URL={publicApiServer}",
                $"-DARG_DEFAULT_KEY={rawDeployToken}",
                $"-DARG_DEFAULT_DISCOVERY={defaultDiscovery}",
                $"-DARG_DEFAULT_MINIMAL={defaultMinimal}",
                "project.nsi"
            ]);

        var binDir = Path.Combine(projectPath, "build", "bin");
        if (!Directory.Exists(binDir))
            throw new InvalidOperationException("Installer output directory not found after build.");

        var installerPath = Directory.GetFiles(binDir, "*-installer.exe")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;

        if (installerPath is null)
            throw new FileNotFoundException("Installer executable was not generated in build/bin.");

        var bytes = await File.ReadAllBytesAsync(installerPath);
        return (bytes, Path.GetFileName(installerPath));
    }

    private string GetBinaryPath()
    {
        return _config["AgentPackage:BinaryPath"]
            ?? throw new InvalidOperationException("AgentPackage:BinaryPath is not configured.");
    }

    private string ResolveWailsPath()
    {
        var configured = _config["AgentPackage:WailsPath"];

        // Se a configuração aponta para um arquivo existente, use diretamente.
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        // Caminhos comuns de instalação (Windows e Linux).
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(userProfile, "go", "bin", "wails.exe"),
            Path.Combine(userProfile, "go", "bin", "wails"),
            "/root/go/bin/wails",
            "/usr/local/bin/wails",
            "/usr/bin/wails",
        };

        var existing = candidates.FirstOrDefault(File.Exists);
        if (existing is not null)
            return existing;

        // Fallback: resolução via PATH do sistema (funciona em Windows e Linux).
        return configured ?? (OperatingSystem.IsWindows() ? "wails.exe" : "wails");
    }

    private string ResolveMakensisPath()
    {
        var configured = _config["AgentPackage:MakensisPath"];

        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        // Caminhos comuns de instalação (Windows e Linux).
        var commonPaths = new[]
        {
            @"C:\Program Files (x86)\NSIS\makensis.exe",
            @"C:\Program Files\NSIS\makensis.exe",
            "/usr/bin/makensis",
            "/usr/local/bin/makensis",
        };

        var existing = commonPaths.FirstOrDefault(File.Exists);
        if (existing is not null)
            return existing;

        // Fallback: resolução via PATH do sistema (funciona em Windows e Linux).
        return configured ?? "makensis";
    }

    private static async Task RunProcessAsync(string fileName, string workingDirectory, string[] arguments, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Could not start process: {fileName}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FileNotFoundException($"Could not start required executable: {fileName}", fileName, ex);
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Process failed ({fileName}). ExitCode={process.ExitCode}.\nOUT: {stdOut}\nERR: {stdErr}");
        }
    }
}
