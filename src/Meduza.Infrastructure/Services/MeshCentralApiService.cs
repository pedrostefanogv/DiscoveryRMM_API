using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meduza.Core.Configuration;
using Meduza.Core.Entities;
using Meduza.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Meduza.Infrastructure.Services;

public class MeshCentralApiService : IMeshCentralApiService
{
    private static readonly Regex InvalidGroupChars = new("[^a-zA-Z0-9._ -]", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MeshCentralOptions _options;
    private readonly ISiteConfigurationRepository _siteConfigurationRepository;

    public MeshCentralApiService(IOptions<MeshCentralOptions> options, ISiteConfigurationRepository siteConfigurationRepository)
    {
        _options = options.Value;
        _siteConfigurationRepository = siteConfigurationRepository;
    }

    public async Task<MeshCentralInstallInstructions> ProvisionInstallAsync(
        Client client,
        Site site,
        string meduzaDeployToken,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("MeshCentral integration is disabled.");

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("MeshCentral BaseUrl is not configured.");

        if (string.IsNullOrWhiteSpace(_options.ApiUsername) || string.IsNullOrWhiteSpace(_options.ApiPassword))
            throw new InvalidOperationException("MeshCentral API credentials are not configured.");

        var groupName = BuildGroupName(client, site);
        using var ws = new ClientWebSocket();
        if (_options.IgnoreTlsErrors)
            ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        var xmeshauth = Convert.ToBase64String(Encoding.UTF8.GetBytes(_options.ApiUsername)) + "," +
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(_options.ApiPassword));
        ws.Options.SetRequestHeader("x-meshauth", xmeshauth);

        var wsUri = BuildControlUri(_options.BaseUrl);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

        await ws.ConnectAsync(wsUri, linked.Token);

        var meshId = await EnsureMeshGroupAsync(ws, groupName, linked.Token);
        var inviteUrl = await CreateInviteLinkAsync(ws, meshId, linked.Token);

        if (string.IsNullOrWhiteSpace(inviteUrl))
            throw new InvalidOperationException("MeshCentral did not return an invite link.");

        var installMode = ResolveInstallMode(_options.InstallExecutionMode);
        var windowsBackground = BuildWindowsCommandBackground(inviteUrl);
        var windowsInteractive = BuildWindowsCommandInteractive(inviteUrl);
        var linuxBackground = BuildLinuxCommandBackground(inviteUrl);
        var linuxInteractive = BuildLinuxCommandInteractive(inviteUrl);

        await PersistSiteMeshBindingAsync(site, groupName, meshId);

        return new MeshCentralInstallInstructions
        {
            GroupName = groupName,
            MeshId = meshId,
            InstallUrl = inviteUrl,
            InstallMode = installMode,
            WindowsCommandBackground = windowsBackground,
            WindowsCommandInteractive = windowsInteractive,
            LinuxCommandBackground = linuxBackground,
            LinuxCommandInteractive = linuxInteractive,
            WindowsCommand = installMode == "interactive" ? windowsInteractive : windowsBackground,
            LinuxCommand = installMode == "interactive" ? linuxInteractive : linuxBackground
        };
    }

    private async Task PersistSiteMeshBindingAsync(Site site, string groupName, string meshId)
    {
        var existing = await _siteConfigurationRepository.GetBySiteIdAsync(site.Id);
        if (existing is null)
        {
            await _siteConfigurationRepository.CreateAsync(new SiteConfiguration
            {
                SiteId = site.Id,
                ClientId = site.ClientId,
                MeshCentralGroupName = groupName,
                MeshCentralMeshId = meshId,
                CreatedBy = "meshcentral-sync",
                UpdatedBy = "meshcentral-sync"
            });
            return;
        }

        existing.MeshCentralGroupName = groupName;
        existing.MeshCentralMeshId = meshId;
        existing.UpdatedBy = "meshcentral-sync";
        await _siteConfigurationRepository.UpdateAsync(existing);
    }

    private static string ResolveInstallMode(string? raw)
    {
        return string.Equals(raw, "interactive", StringComparison.OrdinalIgnoreCase)
            ? "interactive"
            : "background";
    }

    private static string BuildWindowsCommandInteractive(string installUrl)
    {
        return $"powershell -ExecutionPolicy Bypass -Command \"iwr -UseBasicParsing '{installUrl}' -OutFile meshcentral-agent.exe; .\\meshcentral-agent.exe\"";
    }

    private static string BuildWindowsCommandBackground(string installUrl)
    {
        return $"powershell -ExecutionPolicy Bypass -Command \"iwr -UseBasicParsing '{installUrl}' -OutFile meshcentral-agent.exe; Start-Process -FilePath .\\meshcentral-agent.exe -WindowStyle Hidden\"";
    }

    private static string BuildLinuxCommandInteractive(string installUrl)
    {
        return $"curl -fsSL '{installUrl}' | sh";
    }

    private static string BuildLinuxCommandBackground(string installUrl)
    {
        return $"nohup sh -c \"curl -fsSL '{installUrl}' | sh\" >/tmp/meshcentral-agent-install.log 2>&1 &";
    }

    private async Task<string> EnsureMeshGroupAsync(ClientWebSocket ws, string groupName, CancellationToken ct)
    {
        var listResponseId = "meduza.meshes." + Guid.NewGuid().ToString("N");
        await SendAsync(ws, new { action = "meshes", responseid = listResponseId }, ct);

        var meshesReply = await ReadUntilAsync(ws, data =>
        {
            var responseId = data.GetPropertyOrDefault("responseid");
            return string.Equals(responseId, listResponseId, StringComparison.Ordinal);
        }, ct);

        var meshId = FindMeshIdByName(meshesReply, groupName);
        if (!string.IsNullOrWhiteSpace(meshId))
            return meshId;

        var createResponseId = "meduza.createmesh." + Guid.NewGuid().ToString("N");
        await SendAsync(ws, new
        {
            action = "createmesh",
            meshname = groupName,
            meshtype = 2,
            responseid = createResponseId
        }, ct);

        var createReply = await ReadUntilAsync(ws, data =>
        {
            var responseId = data.GetPropertyOrDefault("responseid");
            return string.Equals(responseId, createResponseId, StringComparison.Ordinal);
        }, ct);

        var createResult = createReply.GetPropertyOrDefault("result");
        if (!string.IsNullOrWhiteSpace(createResult) && !string.Equals(createResult, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MeshCentral create group failed: " + createResult);

        var createdMeshId = createReply.GetPropertyOrDefault("meshid");
        if (!string.IsNullOrWhiteSpace(createdMeshId))
            return createdMeshId;

        // Fallback: busca novamente para localizar o id do grupo criado.
        await SendAsync(ws, new { action = "meshes", responseid = listResponseId }, ct);
        var meshesRetry = await ReadUntilAsync(ws, data =>
        {
            var responseId = data.GetPropertyOrDefault("responseid");
            return string.Equals(responseId, listResponseId, StringComparison.Ordinal);
        }, ct);

        meshId = FindMeshIdByName(meshesRetry, groupName);
        if (string.IsNullOrWhiteSpace(meshId))
            throw new InvalidOperationException("MeshCentral group provisioning did not return a mesh id.");

        return meshId;
    }

    private async Task<string> CreateInviteLinkAsync(ClientWebSocket ws, string meshId, CancellationToken ct)
    {
        var inviteResponseId = "meduza.invite." + Guid.NewGuid().ToString("N");
        await SendAsync(ws, new
        {
            action = "createInviteLink",
            meshid = meshId,
            expire = Math.Max(1, _options.InviteExpireHours),
            flags = 0,
            responseid = inviteResponseId
        }, ct);

        var inviteReply = await ReadUntilAsync(ws, data =>
        {
            var responseId = data.GetPropertyOrDefault("responseid");
            return string.Equals(responseId, inviteResponseId, StringComparison.Ordinal);
        }, ct);

        var result = inviteReply.GetPropertyOrDefault("result");
        if (!string.IsNullOrWhiteSpace(result) && !string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MeshCentral invite creation failed: " + result);

        return inviteReply.GetPropertyOrDefault("url") ?? string.Empty;
    }

    private static async Task SendAsync(ClientWebSocket ws, object payload, CancellationToken ct)
    {
        var raw = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(raw);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<JsonElement> ReadUntilAsync(ClientWebSocket ws, Func<JsonElement, bool> predicate, CancellationToken ct)
    {
        while (true)
        {
            var msg = await ReceiveJsonAsync(ws, ct);

            if (msg.TryGetProperty("cause", out var cause) && string.Equals(cause.GetString(), "noauth", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("MeshCentral authentication failed.");

            if (predicate(msg)) return msg;
        }
    }

    private static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var segment = new ArraySegment<byte>(new byte[64 * 1024]);
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await ws.ReceiveAsync(segment, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("MeshCentral websocket closed unexpectedly.");

            ms.Write(segment.Array!, segment.Offset, result.Count);
            if (result.EndOfMessage) break;
        }

        var raw = Encoding.UTF8.GetString(ms.ToArray());
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static string? FindMeshIdByName(JsonElement reply, string groupName)
    {
        if (!reply.TryGetProperty("meshes", out var meshes) || meshes.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var mesh in meshes.EnumerateArray())
        {
            var name = mesh.GetPropertyOrDefault("name");
            if (!string.Equals(name, groupName, StringComparison.OrdinalIgnoreCase))
                continue;

            return mesh.GetPropertyOrDefault("_id") ?? mesh.GetPropertyOrDefault("id");
        }

        return null;
    }

    private static Uri BuildControlUri(string baseUrl)
    {
        var uri = new Uri(baseUrl, UriKind.Absolute);
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? "ws" : "wss",
            Path = "/control.ashx"
        };
        return builder.Uri;
    }

    private static string BuildGroupName(Client client, Site site)
    {
        var clientPart = Normalize(client.Name);
        var sitePart = Normalize(site.Name);
        return $"meduza-{clientPart}-{sitePart}";
    }

    private static string Normalize(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "unknown";

        var sanitized = InvalidGroupChars.Replace(trimmed, "-");
        return Regex.Replace(sanitized, "\\s+", "-").ToLowerInvariant();
    }
}

internal static class MeshCentralJsonExtensions
{
    public static string? GetPropertyOrDefault(this JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var prop)
            ? prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString()
            : null;
    }
}
