using System.Net.Sockets;
using Discovery.Core.Interfaces;
using NATS.Client.Core;

namespace Discovery.Infrastructure.Services;

public class NatsConnectionValidator : INatsConnectionValidator
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public async Task<(bool IsValid, string[] Errors)> ValidateConnectionAsync(
        string hostOrUrl,
        string? user,
        string? password,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(hostOrUrl))
        {
            errors.Add("NATS host cannot be empty.");
            return (false, errors.ToArray());
        }

        if (!string.IsNullOrWhiteSpace(user) && string.IsNullOrWhiteSpace(password))
            errors.Add("NATS user provided without password.");

        if (string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(password))
            errors.Add("NATS password provided without user.");

        if (errors.Count > 0)
            return (false, errors.ToArray());

        if (Uri.TryCreate(hostOrUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            var scheme = uri.Scheme.ToLowerInvariant();
            var host = uri.Host;
            var port = uri.Port > 0 ? uri.Port : 4222;

            if (scheme is "nats" or "tls")
            {
                if (await TryConnectNatsAsync($"{scheme}://{host}:{port}", user, password, cancellationToken))
                    return (true, Array.Empty<string>());

                if (await TryConnectNatsAsync($"wss://{host}:4222", user, password, cancellationToken))
                    return (true, Array.Empty<string>());

                errors.Add($"Unable to connect to NATS via {scheme}://{host}:{port} or wss://{host}:4222.");
                return (false, errors.ToArray());
            }

            if (scheme == "wss")
            {
                if (await TryConnectNatsAsync($"wss://{host}:{port}", user, password, cancellationToken))
                    return (true, Array.Empty<string>());

                errors.Add($"Unable to connect to NATS via wss://{host}:{port}.");
                return (false, errors.ToArray());
            }

            errors.Add("NATS URL must use nats://, tls:// or wss://.");
            return (false, errors.ToArray());
        }

        // Considera host ou hostname puro.
        var hostOnly = hostOrUrl.Trim();
        if (!IsValidHostName(hostOnly))
        {
            errors.Add("NATS host is not valid. Use IP address or DNS name.");
            return (false, errors.ToArray());
        }

        if (await TryConnectNatsAsync($"nats://{hostOnly}:4222", user, password, cancellationToken))
            return (true, Array.Empty<string>());

        if (await TryConnectNatsAsync($"wss://{hostOnly}:4222", user, password, cancellationToken))
            return (true, Array.Empty<string>());

        errors.Add($"Unable to connect to NATS at nats://{hostOnly}:4222 or wss://{hostOnly}:4222.");
        return (false, errors.ToArray());
    }

    private static bool IsValidHostName(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (System.Net.IPAddress.TryParse(host, out _))
            return true;

        // Esta validação cobre nomes DNS simples e com subdomínio.
        var hostType = Uri.CheckHostName(host);
        return hostType == UriHostNameType.Dns || hostType == UriHostNameType.IPv4 || hostType == UriHostNameType.IPv6;
    }

    private static async Task<bool> TryConnectNatsAsync(string url, string? user, string? password, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0)
            return false;

        if (uri.Scheme is "nats" or "tls")
        {
            if (!await TryOpenTcpAsync(uri.Host, uri.Port, cancellationToken))
                return false;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ConnectTimeout);

        try
        {
            var opts = new NatsOpts { Url = url };
            if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(password))
            {
                opts = opts with { AuthOpts = new NatsAuthOpts { Username = user, Password = password } };
            }

            await using var connection = new NatsConnection(opts);
            await connection.PingAsync(timeoutCts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryOpenTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient();
        var connectTask = tcp.ConnectAsync(host, port);
        var timeoutTask = Task.Delay(ConnectTimeout, cancellationToken);
        var completed = await Task.WhenAny(connectTask, timeoutTask);
        return completed == connectTask && tcp.Connected;
    }
}
