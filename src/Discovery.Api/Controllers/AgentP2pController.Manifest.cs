using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoints de manifesto canônico P2P.
/// </summary>
public partial class AgentP2pController
{
    // ─────────────────────────────────────────────────────────────────────
    // POST /api/agent-auth/me/p2p/manifest
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Publica (ou atualiza) o manifesto canônico de chunks P2P.
    /// Valida consistência e retorna 202 Accepted.
    /// </summary>
    [HttpPost("me/p2p/manifest")]
    public async Task<IActionResult> PostManifest(
        [FromBody] P2pManifestRequest? request,
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        if (request is null)
            return BadRequest(new { error = "Payload inválido ou ausente." });

        // 1. Validações de consistência dos chunks
        var errors = ValidateManifestRequest(request);
        if (errors.Count > 0)
            return BadRequest(new { error = "Erros de validação", details = errors });

        // 2. clientId do artifact = clientId do site do agent
        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound(new { error = "Agent not found." });

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        var clientId = site?.ClientId ?? Guid.Empty;

        // 4. Serializar manifest
        var manifestJson = JsonSerializer.Serialize(new
        {
            chunkSize = request.ChunkSizeBytes,
            totalSize = request.TotalSize,
            totalChunks = request.Chunks.Count,
            sha256 = request.Sha256,
            chunks = request.Chunks
        });

        var manifest = new P2pArtifactManifest
        {
            ArtifactId = request.ArtifactId,
            ClientId = clientId,
            ManifestJson = manifestJson,
            Sha256 = request.Sha256,
            TotalSize = request.TotalSize,
            ChunkSize = request.ChunkSizeBytes,
            TotalChunks = request.Chunks.Count,
            GeneratedBy = agentId,
            GeneratedAt = DateTime.UtcNow,
        };

        await _manifestRepo.UpsertAsync(manifest, ct);

        return StatusCode(202, new { received = true });
    }

    // ─────────────────────────────────────────────────────────────────────
    // GET /api/agent-auth/me/p2p/manifest/{artifactId}
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Retorna o manifesto canônico de um artifact, se existir.
    /// </summary>
    [HttpGet("me/p2p/manifest/{artifactId:guid}")]
    public async Task<IActionResult> GetManifest(
        Guid artifactId,
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var manifest = await _manifestRepo.GetByArtifactIdAsync(artifactId, ct);
        if (manifest is null)
            return NotFound(new { error = "Manifesto não encontrado para este artifactId." });

        // Parse do manifest JSON para os chunks
        P2pManifestDataDto? manifestData = null;
        try
        {
            manifestData = JsonSerializer.Deserialize<P2pManifestDataDto>(manifest.ManifestJson);
        }
        catch
        {
            // ignored — retorna sem chunks se falhar parse
        }

        // Resolve artifact name
        var artifactName = await ResolveArtifactNameAsync(artifactId, ct);

        return Ok(new P2pManifestResponse
        {
            ArtifactId = manifest.ArtifactId,
            ArtifactName = artifactName,
            Manifest = manifestData,
            GeneratedAtUtc = manifest.GeneratedAt.ToString("O")
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private async Task<string> ResolveArtifactNameAsync(Guid artifactId, CancellationToken ct)
    {
        // Tenta buscar em WingetPackage ou AppPackage pelo Id
        // Usa DbContext diretamente pois os repositórios não expõem lookup por Id
        var db = HttpContext.RequestServices.GetRequiredService<DiscoveryDbContext>();

        var winget = await db.WingetPackages.FindAsync([artifactId], ct);
        if (winget is not null)
            return winget.Name;

        var app = await db.AppPackages.FindAsync([artifactId], ct);
        if (app is not null)
            return app.Name;

        return artifactId.ToString();
    }

    private static List<P2pErrorDetail> ValidateManifestRequest(P2pManifestRequest request)
    {
        var errors = new List<P2pErrorDetail>();

        if (request.ArtifactId == Guid.Empty)
            errors.Add(new P2pErrorDetail { Field = "artifactId", Code = "FIELD_REQUIRED", Message = "artifactId é obrigatório" });

        if (request.ChunkSizeBytes <= 0)
            errors.Add(new P2pErrorDetail { Field = "chunkSizeBytes", Code = "INVALID_VALUE", Message = "chunkSizeBytes deve ser > 0" });

        if (request.TotalSize <= 0)
            errors.Add(new P2pErrorDetail { Field = "totalSize", Code = "INVALID_VALUE", Message = "totalSize deve ser > 0" });

        if (string.IsNullOrWhiteSpace(request.Sha256))
            errors.Add(new P2pErrorDetail { Field = "sha256", Code = "FIELD_REQUIRED", Message = "sha256 é obrigatório" });
        else if (request.Sha256.Length != 64)
            errors.Add(new P2pErrorDetail { Field = "sha256", Code = "INVALID_LENGTH", Message = "sha256 deve ter 64 caracteres hex" });

        if (request.Chunks.Count == 0)
        {
            errors.Add(new P2pErrorDetail { Field = "chunks", Code = "FIELD_REQUIRED", Message = "chunks não pode ser vazio" });
        }
        else
        {
            // total_chunks * chunk_size >= total_size (último chunk pode ser menor)
            if (request.Chunks.Count * (long)request.ChunkSizeBytes < request.TotalSize - request.ChunkSizeBytes)
                errors.Add(new P2pErrorDetail { Field = "chunks", Code = "INCONSISTENT", Message = "total_chunks * chunk_size insuficiente para cobrir totalSize" });

            for (int i = 0; i < request.Chunks.Count; i++)
            {
                var chunk = request.Chunks[i];

                // chunks[i].offset == i * chunkSize para i < totalChunks - 1
                var expectedOffset = i * (long)request.ChunkSizeBytes;
                if (chunk.Offset != expectedOffset && i < request.Chunks.Count - 1)
                {
                    errors.Add(new P2pErrorDetail
                    {
                        Field = $"chunks[{i}].offset",
                        Code = "INVALID_OFFSET",
                        Message = $"offset esperado {expectedOffset}, recebido {chunk.Offset}"
                    });
                }

                if (chunk.Size <= 0)
                    errors.Add(new P2pErrorDetail { Field = $"chunks[{i}].size", Code = "INVALID_VALUE", Message = "size deve ser > 0" });

                if (string.IsNullOrWhiteSpace(chunk.Sha256) || chunk.Sha256.Length != 64)
                    errors.Add(new P2pErrorDetail { Field = $"chunks[{i}].sha256", Code = "INVALID_LENGTH", Message = "sha256 deve ter 64 caracteres hex" });
            }

            // chunks[last].offset + chunks[last].size == totalSize
            var last = request.Chunks[^1];
            if (last.Offset + last.Size != request.TotalSize)
            {
                errors.Add(new P2pErrorDetail
                {
                    Field = "chunks[last]",
                    Code = "INVALID_LAST_CHUNK",
                    Message = $"last.offset ({last.Offset}) + last.size ({last.Size}) != totalSize ({request.TotalSize})"
                });
            }
        }

        return errors;
    }
}
