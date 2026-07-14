using System.ComponentModel.DataAnnotations;
using Discovery.Core.Cqrs.DeployTokens.Commands;
using Discovery.Core.Cqrs.DeployTokens.Queries;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/deploy-tokens")]
public class DeployTokensController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IDeployTokenService _deployTokenService;
    private readonly IAgentPackageService _agentPackageService;
    private readonly ILogger<DeployTokensController> _logger;

    public DeployTokensController(
        IMediator mediator,
        IDeployTokenService deployTokenService,
        IAgentPackageService agentPackageService,
        ILogger<DeployTokensController> logger)
    {
        _mediator = mediator;
        _deployTokenService = deployTokenService;
        _agentPackageService = agentPackageService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid clientId, [FromQuery] Guid siteId)
    {
        var result = await _mediator.Send(new ListDeployTokensQuery(clientId, siteId));
        return result.ToActionResult();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDeployTokenCommand cmd, CancellationToken ct)
    {
        var result = await _mediator.Send(cmd, ct);

        // Se não for entrega com installer, retorna o DTO normalmente (comportamento legado)
        var delivery = cmd.Delivery?.Trim().ToLowerInvariant();
        if (delivery != "installer" && delivery != "full-installer")
        {
            return result.Match<IActionResult>(
                success: dto => Created("", dto),
                failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
        }

        // Delivery mode: token foi criado, agora gera o instalador binário
        if (result.IsFailure)
        {
            return BadRequest(new { errors = result.Errors.Select(e => new { e.Code, e.Message, e.Field }) });
        }

        var dto = result.Value;
        if (dto == null || string.IsNullOrWhiteSpace(dto.RawToken))
        {
            return StatusCode(500, new { message = "Falha ao gerar instalador: token não retornado." });
        }

        var rawToken = dto.RawToken;
        try
        {
            if (delivery == "full-installer")
            {
                // Instalador completo (offline) — não precisa de internet para instalar
                var (content, fileName) = await _agentPackageService.BuildInstallerAsync(rawToken, cancellationToken: ct);
                _logger.LogInformation("Full installer generated: {FileName} ({Size} bytes) for deploy token prefix={Prefix}",
                    fileName, content.Length, dto.TokenPrefix);
                return File(content, "application/vnd.microsoft.portable-executable", fileName);
            }

            // installer = bootstrap (minimal) — baixa o stage2 da API durante instalação
            var (bootstrapContent, bootstrapFileName) = await _agentPackageService.BuildBootstrapInstallerAsync(rawToken, cancellationToken: ct);
            _logger.LogInformation("Bootstrap installer generated: {FileName} ({Size} bytes) for deploy token prefix={Prefix}",
                bootstrapFileName, bootstrapContent.Length, dto.TokenPrefix);
            return File(bootstrapContent, "application/vnd.microsoft.portable-executable", bootstrapFileName);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Failed to build installer for deploy token prefix={Prefix}", dto.TokenPrefix);
            return StatusCode(503, new { message = "Instalador indisponível temporariamente. Tente novamente em instantes." });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var result = await _mediator.Send(new RevokeDeployTokenCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>
    /// Downloads the installer for a deploy token.
    /// installerType: "online" = bootstrap (minimal) installer, "offline" = portable ZIP package.
    /// </summary>
    [HttpPost("download-installer")]
    public async Task<IActionResult> DownloadInstaller([FromBody] DownloadInstallerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RawToken))
            return BadRequest(new { message = "Token é obrigatório." });

        // Validate token without consuming it (consumption happens later when the agent uses it)
        var token = await _deployTokenService.GetValidatedAsync(request.RawToken);
        if (token is null)
            return Unauthorized(new { message = "Token inválido, expirado, revogado ou sem usos disponíveis." });

        try
        {
            var installerType = request.InstallerType?.Trim().ToLowerInvariant();

            if (installerType == "offline")
            {
                // Portable ZIP package for offline install
                var zipBytes = await _agentPackageService.BuildPortablePackageAsync(request.RawToken, cancellationToken: ct);
                _logger.LogInformation("Portable package generated for deploy token prefix={Prefix}", token.TokenPrefix);
                return File(zipBytes, "application/zip", "discovery-installer-offline.zip");
            }

            // Default: online = bootstrap (minimal) installer
            (byte[] content, string fileName) = await _agentPackageService.BuildBootstrapInstallerAsync(request.RawToken, cancellationToken: ct);
            _logger.LogInformation("Bootstrap installer generated: {FileName} ({Size} bytes) for deploy token prefix={Prefix}",
                fileName, content.Length, token.TokenPrefix);
            return File(content, "application/vnd.microsoft.portable-executable", fileName);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Failed to build installer for deploy token prefix={Prefix}", token.TokenPrefix);
            return StatusCode(503, new { message = "Instalador indisponível temporariamente. Tente novamente em instantes." });
        }
    }

    /// <summary>
    /// Returns available installer options for a given deploy token.
    /// Used by the frontend to determine which installer types are available.
    /// </summary>
    [HttpPost("installer-options")]
    public async Task<IActionResult> GetInstallerOptions([FromBody] DownloadInstallerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RawToken))
            return BadRequest(new { message = "Token é obrigatório." });

        var token = await _deployTokenService.GetValidatedAsync(request.RawToken);
        if (token is null)
            return Unauthorized(new { message = "Token inválido, expirado, revogado ou sem usos disponíveis." });

        return Ok(new[]
        {
            new { type = "online", label = "Instalador mínimo (.exe)", description = "Baixa o instalador completo automaticamente" },
            new { type = "offline", label = "Pacote portátil (.zip)", description = "Download único com binário e configuração" }
        });
    }
}

/// <summary>
/// Request body for downloading an installer with a deploy token.
/// </summary>
public class DownloadInstallerRequest
{
    [Required]
    public string RawToken { get; set; } = string.Empty;

    /// <summary>
    /// "online" = bootstrap (minimal) installer, "offline" = portable ZIP.
    /// Defaults to "online" when not specified.
    /// </summary>
    public string? InstallerType { get; set; }
}
