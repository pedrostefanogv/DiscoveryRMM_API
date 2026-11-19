using Discovery.Core.Cqrs.AgentAuth.Hardware;
using Discovery.Core.Cqrs.AgentAuth.Software;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

public class AgentAuthInventoryController : AgentAuthControllerBase
{
    private readonly ISensorSyncService _sensorSync;
    private readonly ISensorIngestionService _sensorIngestion;

    public AgentAuthInventoryController(
        IMediator mediator,
        IAgentRepository agentRepo,
        ISensorSyncService sensorSync,
        ISensorIngestionService sensorIngestion)
        : base(mediator, agentRepo)
    {
        _sensorSync = sensorSync;
        _sensorIngestion = sensorIngestion;
    }

    // ── Hardware ──────────────────────────────────────────────────────────

    [HttpGet("me/hardware")]
    public async Task<IActionResult> GetHardware()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentHardwareQuery(id)), Ok);
    }

    // M-fix (413 homologação 13/09): o payload de hardware embute o inventário
    // bruto em inventoryRaw (JSON escapado — tamanho 2-4x o original) e pode
    // passar do default do Kestrel (~28,6 MB) com inventários grandes.
    // Limite explícito de 64 MB alinhado ao client_max_body_size 100m do nginx.
    [RequestSizeLimit(64_000_000)]
    [HttpPost("me/hardware")]
    public async Task<IActionResult> ReportHardwarePost([FromBody] ReportAgentHardwareCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, true);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }), _ => Ok());
    }

    [HttpPut("me/hardware")]
    public Task<IActionResult> ReportHardwarePut([FromBody] ReportAgentHardwareCommand cmd) => ReportHardwarePost(cmd);

    // ── Software ──────────────────────────────────────────────────────────

    [HttpGet("me/software")]
    public async Task<IActionResult> GetSoftwareInventory()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAgentSoftwareQuery(id)), Ok);
    }

    [HttpPost("me/software")]
    public async Task<IActionResult> ReportSoftwarePost([FromBody] ReportAgentSoftwareCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, true);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }), _ => Ok(new { Message = "Software inventory updated." }));
    }

    [HttpPut("me/software")]
    public Task<IActionResult> ReportSoftwarePut([FromBody] ReportAgentSoftwareCommand cmd) => ReportSoftwarePost(cmd);

    // ── Windows Update (gerenciador de patches) ──────────────────────────

    /// <summary>Inventário de atualizações do próprio agente (pendentes + snapshot).</summary>
    [HttpGet("me/windows-updates")]
    public async Task<IActionResult> GetWindowsUpdates()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new Discovery.Core.Cqrs.AgentAuth.Patches.GetAgentWindowsUpdatesQuery(id)), Ok);
    }

    /// <summary>Upload do inventário de Windows Update (scan WUA + QFE).</summary>
    [HttpPost("me/windows-updates")]
    public async Task<IActionResult> ReportWindowsUpdatesPost(
        [FromBody] Discovery.Core.Cqrs.AgentAuth.Patches.ReportAgentWindowsUpdatesCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, true);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }), _ => Ok(new { Message = "Windows update inventory updated." }));
    }

    [HttpPut("me/windows-updates")]
    public Task<IActionResult> ReportWindowsUpdatesPut(
        [FromBody] Discovery.Core.Cqrs.AgentAuth.Patches.ReportAgentWindowsUpdatesCommand cmd)
        => ReportWindowsUpdatesPost(cmd);

    /// <summary>Sonda de reinício pendente (endpoint leve, enviado só em mudança).</summary>
    [HttpPost("me/pending-reboot")]
    public async Task<IActionResult> ReportPendingReboot(
        [FromBody] Discovery.Core.Cqrs.AgentAuth.Patches.ReportAgentPendingRebootCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, true);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }), _ => Ok(new { Message = "Pending reboot state updated." }));
    }

    // ── Sensores ──────────────────────────────────────────────────────────

    /// <summary>
    /// Configuração de sensores aplicável ao agente. Fingerprint próprio (não
    /// compartilha a chave do policy-sync de automação); o payload contém apenas
    /// o necessário para COLETAR — limiares e alertas ficam no servidor.
    /// </summary>
    [HttpPost("me/sensors/sync")]
    public async Task<IActionResult> SyncSensors([FromBody] AgentSensorSyncRequest request, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var response = await _sensorSync.SyncAsync(id, request, ct);
        return Ok(response);
    }

    /// <summary>
    /// Ingestão de amostras de sensores (evidência crua + canais já extraídos).
    /// O servidor revalida status/severidade e descarta canais não declarados.
    /// </summary>
    [HttpPost("me/sensors/samples")]
    public async Task<IActionResult> ReportSensorSamples([FromBody] AgentSensorResultBatch batch, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        // O AgentId confiável é o do token, nunca o do corpo.
        batch.AgentId = id;
        var result = await _sensorIngestion.IngestAsync(batch, ct);
        return Ok(new { accepted = result.Accepted, rejected = result.Rejected });
    }
}
