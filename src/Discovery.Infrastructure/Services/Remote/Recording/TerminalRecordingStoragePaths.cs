using Discovery.Core.Configuration;

namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>
/// Resolve o diretório/arquivo do NDJSON de terminal. Reutiliza a raiz da
/// gravação local (Recording:Local:BasePath) por padrão e aceita override em
/// Recording:Terminal:BasePath.
/// </summary>
public static class TerminalRecordingStoragePaths
{
    public static string ResolveBasePath(RemoteAccessOptions options)
    {
        var configured = options.Recording.Terminal.BasePath;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        return Path.Combine(options.Recording.Local.BasePath, "terminal");
    }

    public static string ResolveRelativePath(Guid sessionId) => $"{sessionId:N}.ndjson";

    public static string ResolveFullPath(RemoteAccessOptions options, Guid sessionId, string? storageUrl = null)
    {
        var relative = string.IsNullOrWhiteSpace(storageUrl)
            ? ResolveRelativePath(sessionId)
            : storageUrl.Trim();

        // Normaliza separadores para o SO atual; storage_url é sempre relativo.
        relative = relative.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(ResolveBasePath(options), relative);
    }
}
