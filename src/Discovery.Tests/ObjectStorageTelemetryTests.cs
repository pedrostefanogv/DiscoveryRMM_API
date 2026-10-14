using Discovery.Infrastructure.Services.ObjectStorage;

namespace Discovery.Tests;

/// <summary>
/// Metricas passivas do object storage: custo zero no S3 (nenhuma chamada extra),
/// por isso sao a base do card do dashboard para storage externo.
/// </summary>
public class ObjectStorageTelemetryTests
{
    [Test]
    public void Snapshot_AggregatesPassiveCounters()
    {
        var telemetry = new ObjectStorageTelemetry();

        telemetry.Record("upload", 1024, TimeSpan.FromMilliseconds(10), true);
        telemetry.Record("download", 2048, TimeSpan.FromMilliseconds(30), true);
        telemetry.Record("presign", 0, TimeSpan.FromMilliseconds(5), true);
        telemetry.Record("head", 0, TimeSpan.FromMilliseconds(500), false, "SlowDown");

        var snapshot = telemetry.Snapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Uploads, Is.EqualTo(1));
            Assert.That(snapshot.Downloads, Is.EqualTo(1));
            Assert.That(snapshot.Presigns, Is.EqualTo(1));
            Assert.That(snapshot.Heads, Is.EqualTo(1));
            Assert.That(snapshot.BytesIn, Is.EqualTo(1024));
            Assert.That(snapshot.BytesOut, Is.EqualTo(2048));
            Assert.That(snapshot.Operations, Is.EqualTo(4));
            Assert.That(snapshot.Failures, Is.EqualTo(1));
            Assert.That(snapshot.ErrorRate, Is.EqualTo(25.0));
            Assert.That(snapshot.LastError, Is.EqualTo("SlowDown"));
            Assert.That(snapshot.LastFailureAtUtc, Is.Not.Null);
            Assert.That(snapshot.LastSuccessAtUtc, Is.Not.Null);
            Assert.That(snapshot.P95LatencyMs, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Snapshot_ZeroOperations_DoesNotReportErrorRate()
    {
        var snapshot = new ObjectStorageTelemetry().Snapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Operations, Is.Zero);
            Assert.That(snapshot.ErrorRate, Is.Zero);
            Assert.That(snapshot.P95LatencyMs, Is.Zero);
            Assert.That(snapshot.LastError, Is.Null);
        });
    }

    [Test]
    public void InFlight_IsTracked()
    {
        var telemetry = new ObjectStorageTelemetry();

        telemetry.OperationStarted();
        telemetry.OperationStarted();
        Assert.That(telemetry.Snapshot().InFlight, Is.EqualTo(2));

        telemetry.OperationFinished();
        Assert.That(telemetry.Snapshot().InFlight, Is.EqualTo(1));
    }
}
