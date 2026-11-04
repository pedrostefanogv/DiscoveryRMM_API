using System.Text.Json;
using Discovery.Core.DTOs;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Contrato de fio agente → API. O agente Go (sensors_types.go) serializa em camelCase e
/// a API desserializa com <c>JsonNamingPolicy.CamelCase</c>. Este teste trava o contrato:
/// se um campo for renomeado de um lado só, o dado some silenciosamente (vira null) sem
/// quebrar build nem teste nenhum — exatamente o modo de falha que já ocorreu com
/// <c>powerOnHours</c>.
/// </summary>
[TestFixture]
public class SensorWireContractTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Payload real do agente com TODOS os campos de fase 5 preenchidos.</summary>
    private const string Phase5Payload = """
    {
      "freePercent": 12.5, "usedPercent": 87.5, "totalBytes": 1000, "freeBytes": 125,
      "serviceState": "Running", "processState": "Running", "running": true,
      "processCount": 3, "workingSetBytes": 4096,
      "replies": 4, "sent": 4, "avgRttMs": 12.5, "latencyMinMs": 10, "latencyMaxMs": 15,
      "jitterMs": 1.2, "lossPercent": 0,
      "statusCode": 200, "dnsMs": 1, "connectMs": 2, "tlsMs": 3, "ttfbMs": 4, "totalMs": 9,
      "bytes": 500, "downloadMbps": 100, "uploadMbps": 50, "uptimeSeconds": 86400, "resolveMs": 8,
      "rxBps": 111, "txBps": 222, "readBps": 333, "writeBps": 444, "busyPercent": 55, "diskLatencyMs": 6,
      "exitCode": 0, "timedOut": false, "stdout": "ok", "stderr": "",
      "eventCount": 7, "eventWindowMinutes": 60, "lastEventAt": "2026-11-03T10:00:00Z",
      "smartStatus": "Healthy", "temperatureC": 38.5, "powerOnHours": 12345, "reallocatedSectors": 0,
      "daysToExpiry": 42, "expiresAt": "2026-12-15T00:00:00Z", "certificateSubject": "CN=x",
      "defenderEnabled": true, "defenderRealTimeEnabled": true,
      "signatureAgeDays": 1, "signatureVersion": "1.2.3",
      "bitLockerStatus": "On", "bitLockerEncryptionPercent": 100,
      "restartAttempted": true, "restartSucceeded": true
    }
    """;

    [Test]
    public void Phase5Payload_PopulatesEveryField()
    {
        var measure = JsonSerializer.Deserialize<AgentSensorMeasureDto>(Phase5Payload, Options);
        Assert.That(measure, Is.Not.Null);

        // Se algum destes ficar null, o agente está mandando um nome que a API não conhece.
        var nulls = new List<string>();
        void Check(string name, object? value) { if (value is null) nulls.Add(name); }

        Check(nameof(AgentSensorMeasureDto.EventCount), measure.EventCount);
        Check(nameof(AgentSensorMeasureDto.EventWindowMinutes), measure.EventWindowMinutes);
        Check(nameof(AgentSensorMeasureDto.LastEventAt), measure.LastEventAt);
        Check(nameof(AgentSensorMeasureDto.SmartStatus), measure.SmartStatus);
        Check(nameof(AgentSensorMeasureDto.TemperatureC), measure.TemperatureC);
        Check(nameof(AgentSensorMeasureDto.PowerOnHours), measure.PowerOnHours);
        Check(nameof(AgentSensorMeasureDto.ReallocatedSectors), measure.ReallocatedSectors);
        Check(nameof(AgentSensorMeasureDto.DaysToExpiry), measure.DaysToExpiry);
        Check(nameof(AgentSensorMeasureDto.ExpiresAt), measure.ExpiresAt);
        Check(nameof(AgentSensorMeasureDto.CertificateSubject), measure.CertificateSubject);
        Check(nameof(AgentSensorMeasureDto.DefenderEnabled), measure.DefenderEnabled);
        Check(nameof(AgentSensorMeasureDto.DefenderRealTimeEnabled), measure.DefenderRealTimeEnabled);
        Check(nameof(AgentSensorMeasureDto.SignatureAgeDays), measure.SignatureAgeDays);
        Check(nameof(AgentSensorMeasureDto.SignatureVersion), measure.SignatureVersion);
        Check(nameof(AgentSensorMeasureDto.BitLockerStatus), measure.BitLockerStatus);
        Check(nameof(AgentSensorMeasureDto.BitLockerEncryptionPercent), measure.BitLockerEncryptionPercent);
        Check(nameof(AgentSensorMeasureDto.RestartAttempted), measure.RestartAttempted);
        Check(nameof(AgentSensorMeasureDto.RestartSucceeded), measure.RestartSucceeded);
        Check(nameof(AgentSensorMeasureDto.RxBps), measure.RxBps);
        Check(nameof(AgentSensorMeasureDto.TxBps), measure.TxBps);
        Check(nameof(AgentSensorMeasureDto.ReadBps), measure.ReadBps);
        Check(nameof(AgentSensorMeasureDto.WriteBps), measure.WriteBps);
        Check(nameof(AgentSensorMeasureDto.BusyPercent), measure.BusyPercent);
        Check(nameof(AgentSensorMeasureDto.DiskLatencyMs), measure.DiskLatencyMs);

        Assert.That(nulls, Is.Empty,
            "Campos do agente que chegaram nulos (nome de fio divergente): " + string.Join(", ", nulls));

        // Valores amostrados: garante que não é só "desserializou sem erro".
        Assert.Multiple(() =>
        {
            Assert.That(measure.PowerOnHours, Is.EqualTo(12345));
            Assert.That(measure.EventCount, Is.EqualTo(7));
            Assert.That(measure.BitLockerStatus, Is.EqualTo("On"));
            Assert.That(measure.SmartStatus, Is.EqualTo("Healthy"));
        });
    }
}