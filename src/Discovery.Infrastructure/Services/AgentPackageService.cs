using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class AgentPackageService : IAgentPackageService
{
    private readonly IConfiguration _config;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<AgentPackageService> _logger;
    private static readonly SemaphoreSlim BuildLock = new(1, 1);

    public AgentPackageService(IConfiguration config, IConfigurationService configurationService, ILogger<AgentPackageService> logger)
    {
        _config = config;
        _configurationService = configurationService;
        _logger = logger;
    }

    public async Task PrebuildBaseBinaryAsync(bool forceRebuild = false, CancellationToken cancellationToken = default)
    {
        var activeProfile = GetActiveProfileName();
        var projectPath = GetAgentPackageSetting("DiscoveryProjectPath")
            ?? (OperatingSystem.IsWindows() ? @"C:\Projetos\Discovery" : "/opt/discovery-agent-src");
        
        if (!Directory.Exists(projectPath))
            throw new InvalidOperationException($"Discovery project path does not exist: {projectPath}");

        var binaryPath = GetBinaryPath();
        if (!forceRebuild && File.Exists(binaryPath))
            return;

        _logger.LogInformation(
            "Agent prebuild starting with profile={Profile}, host={Host}, projectPath={ProjectPath}",
            activeProfile,
            OperatingSystem.IsWindows() ? "windows" : "linux",
            projectPath);

        await BuildLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check after waiting for lock.
            if (!forceRebuild && File.Exists(binaryPath))
                return;

            // Clean previous binary so stale artifacts don't survive a failed build.
            if (forceRebuild && File.Exists(binaryPath))
                File.Delete(binaryPath);

            // Use the build script from the agent repository
            if (OperatingSystem.IsWindows())
            {
                await BuildOnWindowsAsync(projectPath, cancellationToken);
            }
            else
            {
                await BuildOnLinuxAsync(projectPath, cancellationToken);
            }

            if (!File.Exists(binaryPath))
                throw new FileNotFoundException("Prebuild finished but binary was not found.", binaryPath);
        }
        finally
        {
            BuildLock.Release();
        }
    }

    private async Task BuildOnLinuxAsync(string projectPath, CancellationToken cancellationToken)
    {
        var buildScriptPath = Path.Combine(projectPath, "build", "server-api", "linux", "build-agent-server-linux.sh");
        if (!File.Exists(buildScriptPath))
            throw new FileNotFoundException($"Build script not found: {buildScriptPath}");

        var outDir = GetAgentPackageSetting("OutputDirectory") ?? Path.Combine(projectPath, "src", "build", "bin");
        var outputName = GetAgentPackageSetting("OutputName") ?? "discovery-agent.exe";

        _logger.LogInformation("Building agent on Linux using script: {BuildScript}", buildScriptPath);

        // GOFLAGS=-buildvcs=false is required when running as root (sudo) because git
        // detects the directory owner mismatch and refuses to embed VCS info.
        var extraEnv = new Dictionary<string, string>
        {
            ["GOFLAGS"] = "-buildvcs=false"
        };

        try
        {
            await RunProcessAsync(
                fileName: "bash",
                workingDirectory: projectPath,
                arguments: [buildScriptPath, "--project-root", projectPath, "--out-dir", outDir, "--output-name", outputName, "--write-installer-json", "0"],
                extraEnvironment: extraEnv,
                cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            if (ex.Message.Contains("go nao encontrado") || ex.Message.Contains("go not found"))
            {
                throw new InvalidOperationException(
                    "Go toolchain is not installed. Required for building the agent on Linux.\n" +
                    "Install Go: sudo apt-get install golang-go\n" +
                    "Or download from: https://golang.org/dl/", ex);
            }
            if (ex.Message.Contains("x86_64-w64-mingw32-gcc") || ex.Message.Contains("MinGW"))
            {
                throw new InvalidOperationException(
                    "MinGW cross-compiler is not installed. Required for building Windows agent on Linux.\n" +
                    "Install: sudo apt-get install gcc-mingw-w64-x86-64 binutils-mingw-w64-x86-64", ex);
            }
            throw;
        }
    }

    private async Task BuildOnWindowsAsync(string projectPath, CancellationToken cancellationToken)
    {
        var buildScriptPath = Path.Combine(projectPath, "build", "scripts", "build-install-installer.ps1");
        if (!File.Exists(buildScriptPath))
            throw new FileNotFoundException($"Build script not found: {buildScriptPath}");

        var outputName = GetAgentPackageSetting("OutputName") ?? "discovery-agent.exe";

        _logger.LogInformation("Building agent on Windows using script: {BuildScript}", buildScriptPath);

        try
        {
            await RunProcessAsync(
                fileName: "powershell.exe",
                workingDirectory: projectPath,
                arguments: ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", buildScriptPath, "-ProjectRoot", projectPath, "-OutputName", outputName],
                cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            if (ex.Message.Contains("'go'") || ex.Message.Contains("Go"))
            {
                throw new InvalidOperationException(
                    "Go toolchain is not installed. Required for building the agent.\n" +
                    "Download from: https://golang.org/dl/", ex);
            }
            throw;
        }
    }

    public async Task<byte[]> BuildPortablePackageAsync(string rawDeployToken)
    {
        var binaryPath = GetBinaryPath();

        var apiScheme = _config["AgentPackage:PublicApiScheme"] ?? "http";

        var apiServer = _config["AgentPackage:PublicApiServer"]
            ?? throw new InvalidOperationException("AgentPackage:PublicApiServer is not configured.");

        var serverConfig = await _configurationService.GetServerConfigAsync();
        var natsHost = !string.IsNullOrWhiteSpace(serverConfig.NatsServerHostExternal)
            ? serverConfig.NatsServerHostExternal
            : serverConfig.NatsServerHostInternal;
        var natsUrl = string.IsNullOrWhiteSpace(natsHost) ? null : natsHost;

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
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Agent binary — store without compression (already a compiled binary)
            var exeEntry = archive.CreateEntry("discovery-discovery.exe", CompressionLevel.NoCompression);
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

        return ms.ToArray();
    }

    public async Task<(byte[] Content, string FileName)> BuildInstallerAsync(string rawDeployToken)
    {
        var activeProfile = GetActiveProfileName();
        var projectPath = GetAgentPackageSetting("DiscoveryProjectPath")
            ?? (OperatingSystem.IsWindows() ? @"C:\Projetos\Discovery" : "/opt/discovery-agent-src");
        
        if (!Directory.Exists(projectPath))
            throw new InvalidOperationException($"Discovery project path does not exist: {projectPath}");

        await PrebuildBaseBinaryAsync();

        return await BuildInstallerWithNsisAsync(projectPath, rawDeployToken, activeProfile);
    }

    private async Task<(byte[] Content, string FileName)> BuildInstallerWithNsisAsync(string projectPath, string rawDeployToken, string activeProfile)
    {
        var installerDirectory = GetAgentPackageSetting("InstallerDirectory")
            ?? Path.Combine("src", "build", "windows", "installer");
        var installerDir = Path.Combine(projectPath, installerDirectory);
        var projectNsi = Path.Combine(installerDir, "project.nsi");
        if (!File.Exists(projectNsi))
            throw new FileNotFoundException("NSIS project file not found.", projectNsi);

        var makensisPath = ResolveMakensisPath();

        var apiScheme = _config["AgentPackage:PublicApiScheme"] ?? "https";
        var publicApiServer = _config["AgentPackage:PublicApiServer"]
            ?? throw new InvalidOperationException("AgentPackage:PublicApiServer is not configured.");
        var publicApiUrl = $"{apiScheme}://{publicApiServer}";

        var defaultDiscovery = (_config["AgentPackage:InstallerDefaults:DiscoveryEnabled"] ?? "1") == "0" ? "0" : "1";
        var defaultMinimal = (_config["AgentPackage:InstallerDefaults:MinimalDefault"] ?? "1") == "0" ? "0" : "1";
        var outputName = GetAgentPackageSetting("InstallerOutputName") ?? "discovery-agent-install.exe";

        _logger.LogInformation(
            "Agent installer build with profile={Profile}, installerDir={InstallerDir}",
            activeProfile,
            installerDir);

        // WebView2 bootstrapper is embedded by the NSIS script (wails_tools.nsh).
        await EnsureWebView2BootstrapperAsync(installerDir);

        var binaryPath = GetBinaryPath();

        await RunProcessAsync(
            fileName: makensisPath,
            workingDirectory: installerDir,
            arguments:
            [
                "-V3",
                "-INPUTCHARSET", "UTF8",
                $"-DARG_WAILS_AMD64_BINARY={binaryPath}",
                $"-DARG_DEFAULT_URL={publicApiUrl}",
                $"-DARG_DEFAULT_KEY={rawDeployToken}",
                $"-DARG_DEFAULT_DISCOVERY={defaultDiscovery}",
                $"-DARG_DEFAULT_MINIMAL={defaultMinimal}",
                $"-DARG_OUTFILE_NAME={outputName}",
                "project.nsi"
            ]);

        var binDir = Path.Combine(projectPath, "src", "build", "bin");
        if (!Directory.Exists(binDir))
            binDir = Path.Combine(projectPath, "build", "bin");

        if (!Directory.Exists(binDir))
            throw new InvalidOperationException("Installer output directory not found after build.");

        var installerPath = Path.Combine(binDir, outputName);
        if (!File.Exists(installerPath))
            throw new FileNotFoundException($"Installer executable was not generated: {installerPath}");

        var bytes = await File.ReadAllBytesAsync(installerPath);
        return (bytes, outputName);
    }

    private async Task EnsureWebView2BootstrapperAsync(string installerDir)
    {
        var tmpDir = Path.Combine(installerDir, "tmp");
        var webview2Path = Path.Combine(tmpDir, "MicrosoftEdgeWebview2Setup.exe");

        if (File.Exists(webview2Path))
            return;

        _logger.LogInformation("Downloading WebView2 bootstrapper for NSIS installer...");
        Directory.CreateDirectory(tmpDir);

        using var http = new HttpClient();
        using var response = await http.GetAsync(
            "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var fs = new FileStream(webview2Path, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fs);
        _logger.LogInformation("WebView2 bootstrapper downloaded to {Path}", webview2Path);
    }

    private async Task<(byte[] Content, string FileName)> BuildInstallerFromBinaryAsync(string rawDeployToken)
    {
        var content = await BuildPortablePackageAsync(rawDeployToken);
        var outputName = GetAgentPackageSetting("OutputName") ?? "discovery-agent.exe";
        var zipName = Path.ChangeExtension(outputName, ".zip");
        return (content, zipName);
    }

    private string GetBinaryPath()
    {
        var configured = GetAgentPackageSetting("BinaryPath");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        // Derive default BinaryPath from DiscoveryProjectPath
        var projectPath = GetAgentPackageSetting("DiscoveryProjectPath")
            ?? (OperatingSystem.IsWindows() ? @"C:\Projetos\Discovery" : "/opt/discovery-agent-src");
        var outputName = GetAgentPackageSetting("OutputName") ?? "discovery-agent.exe";
        return Path.Combine(projectPath, "src", "build", "bin", outputName);
    }

    private string ResolveMakensisPath()
    {
        var configured = GetAgentPackageSetting("MakensisPath");

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

    private string GetActiveProfileName()
    {
        var configured = _config["AgentPackage:ActiveProfile"];
        if (string.IsNullOrWhiteSpace(configured) || string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
            return OperatingSystem.IsWindows() ? "windows" : "linux";

        return configured.Trim().ToLowerInvariant();
    }

    private string? GetAgentPackageSetting(string key)
    {
        var activeProfile = GetActiveProfileName();
        var profileValue = _config[$"AgentPackage:Profiles:{activeProfile}:{key}"];
        if (!string.IsNullOrWhiteSpace(profileValue))
            return profileValue;

        return _config[$"AgentPackage:{key}"];
    }

    private static async Task RunProcessAsync(string fileName, string workingDirectory, string[] arguments, Dictionary<string, string>? extraEnvironment = null, CancellationToken cancellationToken = default)
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

        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
                startInfo.Environment[key] = value;
        }

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
