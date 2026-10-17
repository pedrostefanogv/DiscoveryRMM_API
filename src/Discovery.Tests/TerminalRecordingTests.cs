using System.Text.Json;
using Discovery.Api.DependencyInjection;
using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Messaging;
using Discovery.Infrastructure.Services.Remote.Recording;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Discovery.Tests;

/// <summary>
/// Parser do frame de gravação de terminal: válido, inválido e kind desconhecido.
/// O agente pode evoluir o contrato sem derrubar a ingestão.
/// </summary>
[TestFixture]
public class TerminalRecordingFrameParserTests
{
    [Test]
    public void Parse_valid_output_frame_maps_all_fields()
    {
        const string json = """
            {"data":"hello","seq":7,"timestampMs":1712345678901,"kind":"output","cols":120,"rows":40,"backend":"conpty","shellKind":"powershell"}
            """;

        var result = TerminalRecordingFrameParser.Parse(json);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Frame, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result.Frame!.Data, Is.EqualTo("hello"));
            Assert.That(result.Frame.Seq, Is.EqualTo(7));
            Assert.That(result.Frame.TimestampMs, Is.EqualTo(1712345678901));
            Assert.That(result.Frame.Kind, Is.EqualTo("output"));
            Assert.That(result.Frame.Cols, Is.EqualTo(120));
            Assert.That(result.Frame.Rows, Is.EqualTo(40));
            Assert.That(result.Frame.Backend, Is.EqualTo("conpty"));
            Assert.That(result.Frame.ShellKind, Is.EqualTo("powershell"));
            Assert.That(result.Frame.IsKnownKind, Is.True);
        });
    }

    [TestCase("")]
    [TestCase("not-json")]
    [TestCase("[]")]
    [TestCase("""{"data":"x","timestampMs":1}""")]
    [TestCase("""{"data":"x","seq":1}""")]
    [TestCase("""{"data":42,"seq":1,"timestampMs":1}""")]
    public void Parse_invalid_payload_is_rejected_without_throwing(string json)
    {
        TerminalRecordingFrameParseResult result = default!;

        Assert.DoesNotThrow(() => result = TerminalRecordingFrameParser.Parse(json));
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Error, Is.Not.Empty);
    }

    [Test]
    public void Parse_unknown_kind_is_preserved_as_unknown()
    {
        const string json = """{"data":"","seq":1,"timestampMs":10,"kind":"future-event"}""";

        var result = TerminalRecordingFrameParser.Parse(json);

        Assert.That(result.IsSuccess, Is.True, "kind desconhecido não deve descartar o frame");
        Assert.That(result.Frame!.Kind, Is.EqualTo("unknown"));
        Assert.That(result.Frame.IsKnownKind, Is.False);
    }

    [Test]
    public void BuildNdjsonLine_adds_session_id_and_preserves_payload()
    {
        var sessionId = Guid.NewGuid();
        const string raw = """{"data":"abc","seq":3,"timestampMs":99,"kind":"output"}""";

        var line = TerminalRecordingFrameParser.BuildNdjsonLine(raw, sessionId);

        using var doc = JsonDocument.Parse(line);
        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("sessionId").GetString(), Is.EqualTo(sessionId.ToString("D")));
            Assert.That(doc.RootElement.GetProperty("data").GetString(), Is.EqualTo("abc"));
            Assert.That(doc.RootElement.GetProperty("seq").GetInt64(), Is.EqualTo(3));
        });
    }
}

/// <summary>Rate limit por sessão: FPS, bytes/s e teto total da gravação.</summary>
[TestFixture]
public class TerminalRecordingRateLimiterTests
{
    private sealed class MutableClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan delta) => Now += delta;
    }

    [Test]
    public void Rejects_frames_above_fps_limit_and_resets_after_window()
    {
        var clock = new MutableClock();
        var limiter = new TerminalRecordingRateLimiter(
            new RemoteAccessTerminalRecordingOptions
            {
                MaxFramesPerSecond = 2,
                MaxBytesPerSecond = 0,
                MaxRecordingBytes = 0
            },
            () => clock.Now);

        Assert.That(limiter.TryAcquire(1).Accepted, Is.True);
        Assert.That(limiter.TryAcquire(1).Accepted, Is.True);

        var rejected = limiter.TryAcquire(1);
        Assert.That(rejected.Accepted, Is.False);
        Assert.That(rejected.Reason, Is.EqualTo(TerminalRecordingRateLimitReasons.FramesPerSecond));

        clock.Advance(TimeSpan.FromMilliseconds(1100));
        Assert.That(limiter.TryAcquire(1).Accepted, Is.True);
    }

    [Test]
    public void Rejects_frames_above_bytes_per_second_limit()
    {
        var clock = new MutableClock();
        var limiter = new TerminalRecordingRateLimiter(
            new RemoteAccessTerminalRecordingOptions
            {
                MaxFramesPerSecond = 0,
                MaxBytesPerSecond = 100,
                MaxRecordingBytes = 0
            },
            () => clock.Now);

        Assert.That(limiter.TryAcquire(60).Accepted, Is.True);

        var rejected = limiter.TryAcquire(60);
        Assert.That(rejected.Accepted, Is.False);
        Assert.That(rejected.Reason, Is.EqualTo(TerminalRecordingRateLimitReasons.BytesPerSecond));

        clock.Advance(TimeSpan.FromMilliseconds(1100));
        Assert.That(limiter.TryAcquire(60).Accepted, Is.True);
    }

    [Test]
    public void Rejects_frames_above_total_recording_limit()
    {
        var clock = new MutableClock();
        var limiter = new TerminalRecordingRateLimiter(
            new RemoteAccessTerminalRecordingOptions
            {
                MaxFramesPerSecond = 0,
                MaxBytesPerSecond = 0,
                MaxRecordingBytes = 100
            },
            () => clock.Now);

        Assert.That(limiter.TryAcquire(60).Accepted, Is.True);

        clock.Advance(TimeSpan.FromMinutes(1));
        var rejected = limiter.TryAcquire(60);
        Assert.That(rejected.Accepted, Is.False);
        Assert.That(rejected.Reason, Is.EqualTo(TerminalRecordingRateLimitReasons.RecordingBytes));
        Assert.That(limiter.TotalBytes, Is.EqualTo(60));
    }

    [Test]
    public void RestoreTotalBytes_applies_previous_recording_size()
    {
        var limiter = new TerminalRecordingRateLimiter(
            new RemoteAccessTerminalRecordingOptions
            {
                MaxFramesPerSecond = 0,
                MaxBytesPerSecond = 0,
                MaxRecordingBytes = 100
            });

        limiter.RestoreTotalBytes(90);

        Assert.That(limiter.TryAcquire(20).Accepted, Is.False);
        Assert.That(limiter.TryAcquire(5).Accepted, Is.True);
    }
}

/// <summary>Agregação dos metadados (frames/bytes/dropped/max_fps).</summary>
[TestFixture]
public class TerminalRecordingMetricsTests
{
    [Test]
    public void Aggregates_frames_bytes_dropped_and_max_fps()
    {
        var metrics = new TerminalRecordingMetrics();
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        metrics.RecordFrame(10, start);
        metrics.RecordFrame(20, start.AddMilliseconds(200));
        metrics.RecordFrame(30, start.AddMilliseconds(400));
        metrics.RecordDroppedFrame();

        Assert.Multiple(() =>
        {
            Assert.That(metrics.Frames, Is.EqualTo(3));
            Assert.That(metrics.Bytes, Is.EqualTo(60));
            Assert.That(metrics.DroppedFrames, Is.EqualTo(1));
            Assert.That(metrics.MaxFps, Is.EqualTo(3));
            Assert.That(metrics.LastFrameAtUtc, Is.EqualTo(start.AddMilliseconds(400)));
        });

        metrics.RecordFrame(1, start.AddMilliseconds(1600));
        Assert.That(metrics.MaxFps, Is.EqualTo(3), "a janela de 1s deve reiniciar");
    }

    [Test]
    public void Restore_resumes_persisted_counters()
    {
        var metrics = new TerminalRecordingMetrics();
        metrics.Restore(frames: 10, bytes: 500, droppedFrames: 2, maxFps: 8);

        metrics.RecordFrame(100, DateTime.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.Frames, Is.EqualTo(11));
            Assert.That(metrics.Bytes, Is.EqualTo(600));
            Assert.That(metrics.DroppedFrames, Is.EqualTo(2));
            Assert.That(metrics.MaxFps, Is.GreaterThanOrEqualTo(8));
        });
    }
}

/// <summary>Writer NDJSON: append, ordem, flush e finalize idempotente.</summary>
[TestFixture]
public class TerminalRecordingNdjsonWriterTests
{
    private static string[] ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines.ToArray();
    }

    [Test]
    public async Task Appends_lines_in_order_flushes_and_finalizes_idempotently()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"terminal-ndjson-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "session.ndjson");

        try
        {
            var writer = new TerminalRecordingNdjsonWriter(path, TimeSpan.FromMilliseconds(100));

            await writer.AppendAsync("""{"seq":1,"data":"a"}""");
            await writer.AppendAsync("""{"seq":2,"data":"b"}""");
            await writer.AppendAsync("""{"seq":3,"data":"c"}""");
            await writer.FlushAsync();

            var whileOpen = ReadLinesShared(path);
            Assert.That(whileOpen, Has.Length.EqualTo(3), "flush deve tornar as linhas visíveis antes do finalize");

            await writer.FinalizeAsync();
            await writer.FinalizeAsync(); // idempotente

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // handle exclusivo prova que o writer fechou o arquivo
            }

            var lines = ReadLinesShared(path);
            Assert.That(lines, Is.EqualTo(new[]
            {
                """{"seq":1,"data":"a"}""",
                """{"seq":2,"data":"b"}""",
                """{"seq":3,"data":"c"}"""
            }));

            Assert.That(writer.LinesWritten, Is.EqualTo(3));
            Assert.That(writer.BytesWritten, Is.GreaterThan(0));

        }
        finally
        {
            // No Windows o handle do leitor pode levar alguns ms para fechar;
            // o teste não deve falhar por isso.
            for (var attempt = 0; attempt < 5 && Directory.Exists(directory); attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(50);
                }
            }
        }
    }
}

/// <summary>Guarda de finalize: só a primeira chamada vence.</summary>
[TestFixture]
public class TerminalRecordingFinalizeGuardTests
{
    [Test]
    public void Only_first_finalize_wins()
    {
        var guard = new TerminalRecordingFinalizeGuard();

        Assert.That(guard.TryBeginFinalize(), Is.True);
        Assert.That(guard.TryBeginFinalize(), Is.False);
        Assert.That(guard.IsFinalized, Is.True);
    }
}

/// <summary>Parse do subject e registro de DI/hosted service.</summary>
[TestFixture]
public class TerminalRecordingIngestRegistrationTests
{
    [Test]
    public void Parses_canonical_recording_term_subject()
    {
        var clientId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var subject = $"tenant.{clientId:N}.site.{Guid.NewGuid():N}.agent.{agentId:N}.remote.session.{sessionId:N}.recording.term";

        var parsed = TerminalRecordingIngestService.TryParseSubject(subject, out var parsedClient, out var parsedAgent, out var parsedSession);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.True);
            Assert.That(parsedClient, Is.EqualTo(clientId));
            Assert.That(parsedAgent, Is.EqualTo(agentId));
            Assert.That(parsedSession, Is.EqualTo(sessionId));
        });
    }

    [TestCase("tenant.c.site.s.agent.a.remote.session.x.recording.term.extra")]
    [TestCase("tenant.not-a-guid.site.s.agent.a.remote.session.x.recording.term")]
    [TestCase("tenant.c.site.s.agent.a.remote.session.x.recording.frame")]
    [TestCase("")]
    public void Rejects_non_canonical_subject(string subject)
    {
        Assert.That(
            TerminalRecordingIngestService.TryParseSubject(subject, out _, out _, out _),
            Is.False);
    }

    [Test]
    public void Terminal_recording_service_is_auto_registered()
    {
        var services = new ServiceCollection();

        var registrations = services.AddDiscoveryAutoRegisteredServices();

        Assert.That(
            registrations.Any(r => r.InterfaceType == typeof(ITerminalRecordingService)
                                   && r.ImplementationType == typeof(TerminalRecordingService)),
            Is.True,
            "ITerminalRecordingService precisa ser descoberto pelo auto-registro");
    }

    [Test]
    public void AddTerminalRecordingIngest_registers_coordinator_and_hosted_service()
    {
        var services = new ServiceCollection();

        services.AddTerminalRecordingIngest();

        Assert.That(
            services.Any(sd => sd.ServiceType == typeof(TerminalRecordingCoordinator)
                               && sd.Lifetime == ServiceLifetime.Singleton),
            Is.True,
            "o coordenador mantém estado por sessão e deve ser singleton");

        Assert.That(
            services.Any(sd => sd.ServiceType == typeof(IHostedService)
                               && sd.ImplementationType == typeof(TerminalRecordingIngestService)),
            Is.True,
            "o subscriber NATS deve ser registrado como hosted service");
    }
}
