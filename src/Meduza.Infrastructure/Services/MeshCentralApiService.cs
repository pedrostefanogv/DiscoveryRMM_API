using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meduza.Core.Configuration;
using Meduza.Core.Entities;
using Meduza.Core.Entities.Identity;
using Meduza.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Meduza.Infrastructure.Services;

public class MeshCentralApiService : IMeshCentralApiService
{
    private static readonly Regex InvalidGroupChars = new("[^a-zA-Z0-9._ -]", RegexOptions.Compiled);
    private static readonly Regex InvalidUsernameChars = new("[^a-zA-Z0-9._-]", RegexOptions.Compiled);
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

    public async Task<MeshCentralUserUpsertResult> EnsureUserAsync(
        User user,
        string preferredUsername,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings();
        var username = NormalizeUsername(preferredUsername);

        return await ExecuteWithSocketAsync(async (ws, ct) =>
        {
            var users = await ListUsersAsync(ws, ct);
            var existing = users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                await EditUserAsync(ws, existing.UserId, user.Email, user.FullName, ct);
                return new MeshCentralUserUpsertResult
                {
                    UserId = existing.UserId,
                    Username = existing.Username,
                    Created = false
                };
            }

            var createResult = await AddUserAsync(ws, username, user.Email, user.FullName, ct);
            if (!string.IsNullOrWhiteSpace(createResult.UserId))
            {
                return createResult;
            }

            var afterCreate = await ListUsersAsync(ws, ct);
            var created = afterCreate.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            if (created is null)
                throw new InvalidOperationException($"MeshCentral could not confirm user creation for '{username}'.");

            return new MeshCentralUserUpsertResult
            {
                UserId = created.UserId,
                Username = created.Username,
                Created = true
            };
        }, cancellationToken);
    }

    public async Task<MeshCentralMembershipSyncResult> EnsureUserInMeshAsync(
        string meshUserId,
        string meshId,
        int meshAdminRights = 0,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings();

        return await ExecuteWithSocketAsync(async (ws, ct) =>
        {
            var mesh = await GetMeshByIdAsync(ws, meshId, ct);
            var alreadyBound = mesh.UserLinks.Any(link =>
                string.Equals(link, meshUserId, StringComparison.OrdinalIgnoreCase));

            if (alreadyBound)
            {
                return new MeshCentralMembershipSyncResult
                {
                    UserId = meshUserId,
                    MeshId = meshId,
                    Added = false
                };
            }

            var responseId = "meduza.addmeshuser." + Guid.NewGuid().ToString("N");
            await SendAsync(ws, new
            {
                action = "addmeshuser",
                meshid = meshId,
                userid = meshUserId,
                meshadmin = meshAdminRights,
                responseid = responseId
            }, ct);

            var reply = await ReadUntilAsync(ws, data =>
            {
                var incoming = data.GetPropertyOrDefault("responseid");
                return string.Equals(incoming, responseId, StringComparison.Ordinal);
            }, ct);

            var result = reply.GetPropertyOrDefault("result");
            if (!IsOperationOk(result))
                throw new InvalidOperationException("MeshCentral addmeshuser failed: " + result);

            return new MeshCentralMembershipSyncResult
            {
                UserId = meshUserId,
                MeshId = meshId,
                Added = true
            };
        }, cancellationToken);
    }

    public async Task<MeshCentralMembershipSyncResult> RemoveUserFromMeshAsync(
        string meshUserId,
        string meshId,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings();

        return await ExecuteWithSocketAsync(async (ws, ct) =>
        {
            var mesh = await GetMeshByIdAsync(ws, meshId, ct);
            var alreadyRemoved = !mesh.UserLinks.Any(link =>
                string.Equals(link, meshUserId, StringComparison.OrdinalIgnoreCase));

            if (alreadyRemoved)
            {
                return new MeshCentralMembershipSyncResult
                {
                    UserId = meshUserId,
                    MeshId = meshId,
                    Added = false
                };
            }

            var responseId = "meduza.removemeshuser." + Guid.NewGuid().ToString("N");
            await SendAsync(ws, new
            {
                action = "removemeshuser",
                meshid = meshId,
                userid = meshUserId,
                responseid = responseId
            }, ct);

            var reply = await ReadUntilAsync(ws, data =>
            {
                var incoming = data.GetPropertyOrDefault("responseid");
                return string.Equals(incoming, responseId, StringComparison.Ordinal);
            }, ct);

            var result = reply.GetPropertyOrDefault("result");
            if (!IsOperationOk(result))
                throw new InvalidOperationException("MeshCentral removemeshuser failed: " + result);

            return new MeshCentralMembershipSyncResult
            {
                UserId = meshUserId,
                MeshId = meshId,
                Added = false
            };
        }, cancellationToken);
    }

    public async Task DeleteUserAsync(string meshUserId, CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings();

        await ExecuteWithSocketAsync(async (ws, ct) =>
        {
            var responseId = "meduza.deleteuser." + Guid.NewGuid().ToString("N");
            await SendAsync(ws, new
            {
                action = "deleteuser",
                userid = meshUserId,
                responseid = responseId
            }, ct);

            var reply = await ReadUntilAsync(ws, data =>
            {
                var incoming = data.GetPropertyOrDefault("responseid");
                return string.Equals(incoming, responseId, StringComparison.Ordinal);
            }, ct);

            var result = reply.GetPropertyOrDefault("result");
            if (!IsOperationOk(result))
                throw new InvalidOperationException("MeshCentral deleteuser failed: " + result);

            return true;
        }, cancellationToken);
    }

    private void ValidateConnectionSettings()
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("MeshCentral integration is disabled.");

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new InvalidOperationException("MeshCentral BaseUrl is not configured.");

        if (string.IsNullOrWhiteSpace(_options.ApiUsername) || string.IsNullOrWhiteSpace(_options.ApiPassword))
            throw new InvalidOperationException("MeshCentral API credentials are not configured.");
    }

    private async Task<T> ExecuteWithSocketAsync<T>(Func<ClientWebSocket, CancellationToken, Task<T>> callback, CancellationToken cancellationToken)
    {
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
        return await callback(ws, linked.Token);
    }

    private async Task<IReadOnlyCollection<MeshCentralUserRef>> ListUsersAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var responseId = "meduza.users." + Guid.NewGuid().ToString("N");
        await SendAsync(ws, new { action = "users", responseid = responseId }, ct);

        var reply = await ReadUntilAsync(ws, data =>
        {
            var incoming = data.GetPropertyOrDefault("responseid");
            return string.Equals(incoming, responseId, StringComparison.Ordinal);
        }, ct);

        if (!reply.TryGetProperty("users", out var usersNode))
            return [];

        var users = new List<MeshCentralUserRef>();

        if (usersNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in usersNode.EnumerateArray())
            {
                var mapped = ParseUser(item);
                if (mapped is not null)
                    users.Add(mapped);
            }

            return users;
        }

        if (usersNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in usersNode.EnumerateObject())
            {
                var mapped = ParseUser(property.Value, property.Name);
                if (mapped is not null)
                    users.Add(mapped);
            }
        }

        return users;
    }

    private async Task<MeshCentralUserUpsertResult> AddUserAsync(ClientWebSocket ws, string username, string email, string fullName, CancellationToken ct)
    {
        var responseId = "meduza.adduser." + Guid.NewGuid().ToString("N");
        await SendAsync(ws, new
        {
            action = "adduser",
            username,
            pass = GenerateProvisionPassword(),
            email,
            realname = fullName,
            responseid = responseId
        }, ct);

        var reply = await ReadUntilAsync(ws, data =>
        {
            var incoming = data.GetPropertyOrDefault("responseid");
            return string.Equals(incoming, responseId, StringComparison.Ordinal);
        }, ct);

        var result = reply.GetPropertyOrDefault("result");
        if (!IsOperationOk(result))
            throw new InvalidOperationException("MeshCentral adduser failed: " + result);

        var userId = reply.GetPropertyOrDefault("userid")
                     ?? reply.GetPropertyOrDefault("id")
                     ?? reply.GetPropertyOrDefault("_id")
                     ?? string.Empty;

        return new MeshCentralUserUpsertResult
        {
            UserId = userId,
            Username = username,
            Created = true
        };
    }

    private async Task EditUserAsync(ClientWebSocket ws, string userId, string email, string fullName, CancellationToken ct)
    {
        var responseId = "meduza.edituser." + Guid.NewGuid().ToString("N");
        await SendAsync(ws, new
        {
            action = "edituser",
            userid = userId,
            email,
            realname = fullName,
            responseid = responseId
        }, ct);

        var reply = await ReadUntilAsync(ws, data =>
        {
            var incoming = data.GetPropertyOrDefault("responseid");
            return string.Equals(incoming, responseId, StringComparison.Ordinal);
        }, ct);

        var result = reply.GetPropertyOrDefault("result");
        if (!IsOperationOk(result))
            throw new InvalidOperationException("MeshCentral edituser failed: " + result);
    }

    private async Task<MeshCentralMeshRef> GetMeshByIdAsync(ClientWebSocket ws, string meshId, CancellationToken ct)
    {
        var responseId = "meduza.meshes." + Guid.NewGuid().ToString("N");
        await SendAsync(ws, new { action = "meshes", responseid = responseId }, ct);

        var reply = await ReadUntilAsync(ws, data =>
        {
            var incoming = data.GetPropertyOrDefault("responseid");
            return string.Equals(incoming, responseId, StringComparison.Ordinal);
        }, ct);

        if (!reply.TryGetProperty("meshes", out var meshesNode) || meshesNode.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("MeshCentral did not return meshes payload.");

        foreach (var mesh in meshesNode.EnumerateArray())
        {
            var id = mesh.GetPropertyOrDefault("_id") ?? mesh.GetPropertyOrDefault("id");
            if (!string.Equals(id, meshId, StringComparison.OrdinalIgnoreCase))
                continue;

            var links = new List<string>();
            if (mesh.TryGetProperty("links", out var linksNode) && linksNode.ValueKind == JsonValueKind.Object)
            {
                foreach (var link in linksNode.EnumerateObject())
                {
                    if (link.Name.StartsWith("user/", StringComparison.OrdinalIgnoreCase))
                        links.Add(link.Name);
                }
            }

            return new MeshCentralMeshRef(id!, links);
        }

        throw new InvalidOperationException($"MeshCentral mesh '{meshId}' was not found.");
    }

    private static MeshCentralUserRef? ParseUser(JsonElement element, string? fallbackUserId = null)
    {
        var userId = element.GetPropertyOrDefault("_id")
                     ?? element.GetPropertyOrDefault("id")
                     ?? element.GetPropertyOrDefault("userid")
                     ?? fallbackUserId;

        var username = element.GetPropertyOrDefault("name")
                       ?? element.GetPropertyOrDefault("username")
                       ?? userId?.Split('/').LastOrDefault();

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(username))
            return null;

        return new MeshCentralUserRef(userId, username);
    }

    private static string NormalizeUsername(string username)
    {
        var trimmed = username.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new InvalidOperationException("MeshCentral username cannot be empty.");

        var sanitized = InvalidUsernameChars.Replace(trimmed, "-");
        sanitized = Regex.Replace(sanitized, "-+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(sanitized))
            throw new InvalidOperationException("MeshCentral username is invalid after normalization.");

        return sanitized;
    }

    private static bool IsOperationOk(string? result)
    {
        return string.IsNullOrWhiteSpace(result)
               || string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)
               || string.Equals(result, "OK", StringComparison.OrdinalIgnoreCase);
    }

    private static string GenerateProvisionPassword()
    {
        return Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + "!A1";
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

internal sealed record MeshCentralUserRef(string UserId, string Username);
internal sealed record MeshCentralMeshRef(string MeshId, IReadOnlyCollection<string> UserLinks);

internal static class MeshCentralJsonExtensions
{
    public static string? GetPropertyOrDefault(this JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var prop)
            ? prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString()
            : null;
    }
}
