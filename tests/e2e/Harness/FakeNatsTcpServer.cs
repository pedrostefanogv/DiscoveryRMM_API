using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Discovery.E2E.Tests.Harness;

public sealed record FakeNatsMessage(
    string Subject,
    string? ReplyTo,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Payload,
    DateTime ReceivedAtUtc)
{
    public string PayloadText => Encoding.UTF8.GetString(Payload);
}

/// <summary>
/// Real in-process NATS wire-protocol TCP server (TcpListener on 127.0.0.1:0)
/// supporting INFO, CONNECT, PING/PONG, SUB/UNSUB, PUB/HPUB, MSG/HMSG,
/// wildcard '*' / '>' routing, and request/reply to drive real NATS.Net clients.
/// </summary>
public sealed class FakeNatsTcpServer : IAsyncDisposable
{
    private sealed class ClientSession : IAsyncDisposable
    {
        public required TcpClient Tcp { get; init; }
        public required NetworkStream Stream { get; init; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public ConcurrentDictionary<string, string> Subscriptions { get; } = new(StringComparer.Ordinal);

        public async ValueTask DisposeAsync()
        {
            try { Tcp.Close(); } catch { }
            WriteLock.Dispose();
            await ValueTask.CompletedTask;
        }
    }

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, ClientSession> _clients = new();
    private readonly ConcurrentQueue<FakeNatsMessage> _published = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<FakeNatsMessage>> _pendingReplies = new(StringComparer.Ordinal);
    private readonly List<Action<string>> _subWaiters = new();
    private readonly List<Action<FakeNatsMessage>> _pubWaiters = new();
    private readonly object _waiterLock = new();
    private Task? _acceptLoop;

    public int Port { get; }
    public string Url => $"nats://127.0.0.1:{Port}";
    public string ServerId { get; } = "NFAKE2E00000000000000000000000000000000000000000000000000";

    public IReadOnlyCollection<FakeNatsMessage> PublishedMessages => _published.ToArray();

    public FakeNatsTcpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                tcp.NoDelay = true;
                var session = new ClientSession
                {
                    Tcp = tcp,
                    Stream = tcp.GetStream()
                };
                var id = Guid.NewGuid();
                _clients[id] = session;
                _ = Task.Run(() => HandleClientAsync(id, session));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // Keep accept loop alive for other clients.
            }
        }
    }

    private async Task HandleClientAsync(Guid clientId, ClientSession session)
    {
        try
        {
            var infoJson =
                $"{{\"server_id\":\"{ServerId}\",\"server_name\":\"fake-e2e-nats\",\"version\":\"2.10.0\",\"proto\":1,\"headers\":true,\"max_payload\":1048576,\"host\":\"127.0.0.1\",\"port\":{Port}}}";
            await SendRawAsync(session, Encoding.ASCII.GetBytes($"INFO {infoJson}\r\n"), _cts.Token);

            var reader = new WireReader(session.Stream);
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null)
                    break;

                if (line.Length == 0)
                    continue;

                if (line.Equals("PING", StringComparison.OrdinalIgnoreCase))
                {
                    await SendRawAsync(session, "PONG\r\n"u8.ToArray(), _cts.Token);
                    continue;
                }

                if (line.Equals("PONG", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (line.StartsWith("SUB ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var subject = parts[0];
                        var sid = parts[^1];
                        session.Subscriptions[sid] = subject;
                        NotifySubWaiters(subject);
                    }
                    continue;
                }

                if (line.StartsWith("UNSUB ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line[6..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1 && parts.Length == 1)
                    {
                        session.Subscriptions.TryRemove(parts[0], out _);
                    }
                    continue;
                }

                if (line.StartsWith("PUB ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length is 2 or 3 && int.TryParse(parts[^1], out var numBytes))
                    {
                        var subject = parts[0];
                        var replyTo = parts.Length == 3 ? parts[1] : null;
                        var payload = await reader.ReadExactBytesAsync(numBytes, _cts.Token);
                        await reader.ConsumeCrLfAsync(_cts.Token);

                        var msg = new FakeNatsMessage(subject, replyTo, new Dictionary<string, string>(), payload, DateTime.UtcNow);
                        await OnClientPublishedAsync(msg);
                    }
                    continue;
                }

                if (line.StartsWith("HPUB ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length is 3 or 4
                        && int.TryParse(parts[^2], out var headerBytesLen)
                        && int.TryParse(parts[^1], out var totalBytesLen))
                    {
                        var subject = parts[0];
                        var replyTo = parts.Length == 4 ? parts[1] : null;
                        var raw = await reader.ReadExactBytesAsync(totalBytesLen, _cts.Token);
                        await reader.ConsumeCrLfAsync(_cts.Token);

                        var headerBytes = raw.AsSpan(0, headerBytesLen);
                        var payload = raw.AsSpan(headerBytesLen).ToArray();
                        var headers = ParseNatsHeaders(headerBytes);

                        var msg = new FakeNatsMessage(subject, replyTo, headers, payload, DateTime.UtcNow);
                        await OnClientPublishedAsync(msg);
                    }
                }
            }
        }
        catch
        {
            // Client disconnected.
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            await session.DisposeAsync();
        }
    }

    private async Task OnClientPublishedAsync(FakeNatsMessage msg)
    {
        _published.Enqueue(msg);
        NotifyPubWaiters(msg);

        if (_pendingReplies.TryRemove(msg.Subject, out var tcs))
        {
            tcs.TrySetResult(msg);
        }

        await DispatchToSubscribersAsync(msg.Subject, msg.ReplyTo, msg.Headers, msg.Payload);
    }

    public async Task PublishAsync(
        string subject,
        string payload,
        string? replyTo = null,
        IReadOnlyDictionary<string, string>? headers = null)
        => await PublishAsync(subject, Encoding.UTF8.GetBytes(payload), replyTo, headers);

    public async Task PublishAsync(
        string subject,
        byte[] payload,
        string? replyTo = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        await DispatchToSubscribersAsync(subject, replyTo, headers, payload);
    }

    public async Task<FakeNatsMessage> RequestAsync(
        string subject,
        byte[] payload,
        IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? timeout = null)
    {
        var replyInbox = $"_INBOX.FAKE.{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<FakeNatsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingReplies[replyInbox] = tcs;

        try
        {
            await DispatchToSubscribersAsync(subject, replyInbox, headers, payload);
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
            return await tcs.Task;
        }
        finally
        {
            _pendingReplies.TryRemove(replyInbox, out _);
        }
    }

    public async Task WaitForSubscriptionAsync(string subjectOrPattern, TimeSpan? timeout = null)
    {
        if (HasMatchingSubscriber(subjectOrPattern))
            return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Callback(string sub)
        {
            if (SubjectMatches(sub, subjectOrPattern) || SubjectMatches(subjectOrPattern, sub) || string.Equals(sub, subjectOrPattern, StringComparison.Ordinal))
                tcs.TrySetResult();
        }

        lock (_waiterLock)
            _subWaiters.Add(Callback);

        try
        {
            if (HasMatchingSubscriber(subjectOrPattern))
                return;

            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
            await tcs.Task;
        }
        finally
        {
            lock (_waiterLock)
                _subWaiters.Remove(Callback);
        }
    }

    public async Task<FakeNatsMessage> WaitForPublishedAsync(Func<FakeNatsMessage, bool> predicate, TimeSpan? timeout = null)
    {
        var existing = _published.FirstOrDefault(predicate);
        if (existing is not null)
            return existing;

        var tcs = new TaskCompletionSource<FakeNatsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Callback(FakeNatsMessage msg)
        {
            if (predicate(msg))
                tcs.TrySetResult(msg);
        }

        lock (_waiterLock)
            _pubWaiters.Add(Callback);

        try
        {
            existing = _published.FirstOrDefault(predicate);
            if (existing is not null)
                return existing;

            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
            return await tcs.Task;
        }
        finally
        {
            lock (_waiterLock)
                _pubWaiters.Remove(Callback);
        }
    }

    public void ClearPublished()
    {
        while (_published.TryDequeue(out _)) { }
    }

    private bool HasMatchingSubscriber(string subject)
    {
        foreach (var client in _clients.Values)
        {
            foreach (var pattern in client.Subscriptions.Values)
            {
                if (SubjectMatches(pattern, subject) || string.Equals(pattern, subject, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private void NotifySubWaiters(string subject)
    {
        Action<string>[] waiters;
        lock (_waiterLock)
            waiters = _subWaiters.ToArray();
        foreach (var w in waiters)
            w(subject);
    }

    private void NotifyPubWaiters(FakeNatsMessage msg)
    {
        Action<FakeNatsMessage>[] waiters;
        lock (_waiterLock)
            waiters = _pubWaiters.ToArray();
        foreach (var w in waiters)
            w(msg);
    }

    private async Task DispatchToSubscribersAsync(
        string subject,
        string? replyTo,
        IReadOnlyDictionary<string, string>? headers,
        byte[] payload)
    {
        var headerBytes = BuildNatsHeadersBytes(headers);

        foreach (var client in _clients.Values)
        {
            foreach (var (sid, pattern) in client.Subscriptions)
            {
                if (!SubjectMatches(pattern, subject))
                    continue;

                byte[] frame;
                if (headerBytes.Length > 0)
                {
                    var totalLen = headerBytes.Length + payload.Length;
                    var prefix = replyTo is null
                        ? $"HMSG {subject} {sid} {headerBytes.Length} {totalLen}\r\n"
                        : $"HMSG {subject} {sid} {replyTo} {headerBytes.Length} {totalLen}\r\n";
                    var prefixBytes = Encoding.ASCII.GetBytes(prefix);
                    frame = new byte[prefixBytes.Length + headerBytes.Length + payload.Length + 2];
                    Buffer.BlockCopy(prefixBytes, 0, frame, 0, prefixBytes.Length);
                    Buffer.BlockCopy(headerBytes, 0, frame, prefixBytes.Length, headerBytes.Length);
                    Buffer.BlockCopy(payload, 0, frame, prefixBytes.Length + headerBytes.Length, payload.Length);
                    frame[^2] = (byte)'\r';
                    frame[^1] = (byte)'\n';
                }
                else
                {
                    var prefix = replyTo is null
                        ? $"MSG {subject} {sid} {payload.Length}\r\n"
                        : $"MSG {subject} {sid} {replyTo} {payload.Length}\r\n";
                    var prefixBytes = Encoding.ASCII.GetBytes(prefix);
                    frame = new byte[prefixBytes.Length + payload.Length + 2];
                    Buffer.BlockCopy(prefixBytes, 0, frame, 0, prefixBytes.Length);
                    Buffer.BlockCopy(payload, 0, frame, prefixBytes.Length, payload.Length);
                    frame[^2] = (byte)'\r';
                    frame[^1] = (byte)'\n';
                }

                await SendRawAsync(client, frame, _cts.Token);
            }
        }
    }

    private static async Task SendRawAsync(ClientSession session, byte[] bytes, CancellationToken ct)
    {
        await session.WriteLock.WaitAsync(ct);
        try
        {
            await session.Stream.WriteAsync(bytes, ct);
            await session.Stream.FlushAsync(ct);
        }
        catch
        {
            // Ignore closed client stream.
        }
        finally
        {
            session.WriteLock.Release();
        }
    }

    public static bool SubjectMatches(string pattern, string subject)
    {
        if (string.Equals(pattern, subject, StringComparison.Ordinal))
            return true;

        var pTokens = pattern.Split('.');
        var sTokens = subject.Split('.');

        for (var i = 0; i < pTokens.Length; i++)
        {
            var pt = pTokens[i];
            if (pt == ">" && i == pTokens.Length - 1)
                return sTokens.Length > i;

            if (i >= sTokens.Length)
                return false;

            if (pt != "*" && !string.Equals(pt, sTokens[i], StringComparison.Ordinal))
                return false;
        }

        return pTokens.Length == sTokens.Length;
    }

    private static byte[] BuildNatsHeadersBytes(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return [];

        var sb = new StringBuilder("NATS/1.0\r\n");
        foreach (var kv in headers)
            sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        sb.Append("\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static Dictionary<string, string> ParseNatsHeaders(ReadOnlySpan<byte> headerBytes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headerBytes.IsEmpty)
            return result;

        var text = Encoding.UTF8.GetString(headerBytes);
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("NATS/", StringComparison.OrdinalIgnoreCase))
                continue;

            var idx = line.IndexOf(':');
            if (idx > 0)
            {
                var key = line[..idx].Trim();
                var val = line[(idx + 1)..].Trim();
                result[key] = val;
            }
        }
        return result;
    }

    private sealed class WireReader(NetworkStream stream)
    {
        private readonly byte[] _buf = new byte[8192];
        private int _pos;
        private int _len;

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            using var ms = new MemoryStream();
            while (true)
            {
                if (_pos >= _len)
                {
                    _len = await stream.ReadAsync(_buf, ct);
                    _pos = 0;
                    if (_len == 0)
                        return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r');
                }

                var b = _buf[_pos++];
                if (b == (byte)'\n')
                {
                    var arr = ms.ToArray();
                    var count = arr.Length > 0 && arr[^1] == (byte)'\r' ? arr.Length - 1 : arr.Length;
                    return Encoding.UTF8.GetString(arr, 0, count);
                }
                ms.WriteByte(b);
            }
        }

        public async Task<byte[]> ReadExactBytesAsync(int count, CancellationToken ct)
        {
            var result = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                if (_pos < _len)
                {
                    var take = Math.Min(count - offset, _len - _pos);
                    Buffer.BlockCopy(_buf, _pos, result, offset, take);
                    _pos += take;
                    offset += take;
                }
                else
                {
                    _len = await stream.ReadAsync(_buf, ct);
                    _pos = 0;
                    if (_len == 0)
                        throw new EndOfStreamException("Unexpected EOF while reading NATS payload.");
                }
            }
            return result;
        }

        public async Task ConsumeCrLfAsync(CancellationToken ct)
        {
            var crlf = await ReadExactBytesAsync(2, ct);
            if (crlf[0] != (byte)'\r' || crlf[1] != (byte)'\n')
                throw new InvalidDataException("Expected CRLF after NATS payload.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        foreach (var c in _clients.Values)
            await c.DisposeAsync();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { }
        }
        _cts.Dispose();
    }
}
