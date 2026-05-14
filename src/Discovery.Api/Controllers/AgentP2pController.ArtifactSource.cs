using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoint de URL canônica de artifact P2P.
/// </summary>
public partial class AgentP2pController
{
    // ─────────────────────────────────────────────────────────────────────
    // GET /api/agent-auth/me/p2p/artifact-source?artifactId=<guid>[&arch=x64]
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Retorna URL canônica e metadados de integridade de um artifact.
    /// Resolve primeiro em AppPackage, depois em WingetPackage.
    /// </summary>
    [HttpGet("me/p2p/artifact-source")]
    public async Task<IActionResult> GetArtifactSource(
        [FromQuery] Guid artifactId,
        [FromQuery] string arch = "x64",
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        if (artifactId == Guid.Empty)
            return BadRequest(new { error = "artifactId é obrigatório." });

        var db = HttpContext.RequestServices.GetRequiredService<DiscoveryDbContext>();

        // 1. Buscar em AppPackage
        var appPackage = await db.AppPackages.FindAsync([artifactId], ct);
        if (appPackage is not null && !string.IsNullOrWhiteSpace(appPackage.FilePublicUrl))
        {
            return Ok(new P2pArtifactSourceResponse
            {
                ArtifactId = artifactId,
                ArtifactName = appPackage.Name ?? artifactId.ToString(),
                DownloadUrl = appPackage.FilePublicUrl,
                Sha256 = appPackage.FileChecksum ?? string.Empty,
                SizeBytes = appPackage.FileSizeBytes ?? 0,
                Source = "app-package",
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30).ToString("O")
            });
        }

        // 2. Buscar em WingetPackage
        var wingetPackage = await db.WingetPackages.FindAsync([artifactId], ct);
        if (wingetPackage is not null)
        {
            var installerUrl = ResolveWingetInstallerUrl(wingetPackage.InstallerUrlsJson, arch);
            if (!string.IsNullOrWhiteSpace(installerUrl))
            {
                return Ok(new P2pArtifactSourceResponse
                {
                    ArtifactId = artifactId,
                    ArtifactName = wingetPackage.Name ?? artifactId.ToString(),
                    DownloadUrl = installerUrl,
                    Sha256 = string.Empty, // Winget não tem checksum por package
                    SizeBytes = 0,          // Winget não expõe tamanho
                    Source = "winget",
                    ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30).ToString("O")
                });
            }
        }

        // 3. Não encontrado
        return NotFound(new
        {
            error = "Artifact not found",
            message = $"Nenhum artifact encontrado para o ID {artifactId} em AppPackage ou WingetPackage."
        });
    }

    /// <summary>
    /// Extrai a URL do instalador para a arquitetura solicitada do JSON de installers.
    /// Formato esperado: { "x64": "https://...", "x86": "https://...", "arm64": "https://..." }
    /// </summary>
    private static string? ResolveWingetInstallerUrl(string installerUrlsJson, string arch)
    {
        if (string.IsNullOrWhiteSpace(installerUrlsJson) || installerUrlsJson == "{}")
            return null;

        try
        {
            var urls = JsonSerializer.Deserialize<Dictionary<string, string>>(installerUrlsJson);
            if (urls is null) return null;

            // Tenta arquitetura exata, depois fallback para x64
            if (urls.TryGetValue(arch, out var url) && !string.IsNullOrWhiteSpace(url))
                return url;

            if (urls.TryGetValue("x64", out var fallback) && !string.IsNullOrWhiteSpace(fallback))
                return fallback;

            // Qualquer URL disponível
            return urls.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }
        catch
        {
            return null;
        }
    }
}
