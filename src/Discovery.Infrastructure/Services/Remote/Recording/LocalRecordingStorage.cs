using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Discovery.Core.Configuration;

namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>
/// Storage local para gravações (disco do servidor).
/// </summary>
public class LocalRecordingStorage
{
    private readonly RemoteAccessOptions _options;
    private readonly ILogger<LocalRecordingStorage> _logger;

    public LocalRecordingStorage(
        IOptions<RemoteAccessOptions> options,
        ILogger<LocalRecordingStorage> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task SaveAsync(string path, byte[] data, CancellationToken ct = default)
    {
        var basePath = _options.Recording.Local.BasePath;
        var fullPath = Path.Combine(basePath, path);

        var dir = Path.GetDirectoryName(fullPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _logger.LogDebug("Salvando gravação local: {Path} ({Bytes} bytes)", fullPath, data.Length);
        return File.WriteAllBytesAsync(fullPath, data, ct);
    }

    public Task<byte[]> ReadAsync(string path, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(_options.Recording.Local.BasePath, path);
        return File.ReadAllBytesAsync(fullPath, ct);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var fullPath = Path.Combine(_options.Recording.Local.BasePath, path);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.LogInformation("Gravação local deletada: {Path}", fullPath);
        }
        return Task.CompletedTask;
    }

    public Task<long> GetDiskUsageAsync(CancellationToken ct = default)
    {
        var basePath = _options.Recording.Local.BasePath;
        if (!Directory.Exists(basePath))
            return Task.FromResult(0L);

        var totalSize = Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);

        return Task.FromResult(totalSize);
    }
}
