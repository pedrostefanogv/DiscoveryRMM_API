using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Avaliação de sensores é o ponto ÚNICO de decisão de status/severidade
/// (o agente só envia evidência). Estes testes cobrem as bordas de limiar em
/// float e o mapa de exit codes — sem `round` antes da comparação (bug clássico
/// do Tactical na borda do limiar).
/// </summary>
public class SensorEvaluationTests
{
    private static SensorDefinition Disk(double warning = 15, double critical = 10) => new()
    {
        Name = "Disco C:",
        SensorType = SensorType.DiskSpace,
        Target = "C:",
        Comparison = SensorComparison.LowerIsWorse,
        ThresholdUnit = SensorChannelUnit.Percent,
        WarningThreshold = warning,
        CriticalThreshold = critical
    };

    private static AgentSensorMeasureDto DiskMeasure(double freePercent) => new()
    {
        FreePercent = freePercent,
        FreeBytes = freePercent * 10,
        TotalBytes = 1000
    };

    [TestCase(20, SensorStatus.Passing)]
    [TestCase(15.0001, SensorStatus.Passing)]
    [TestCase(15, SensorStatus.Passing)]      // comparação estrita: igual ao limiar de atenção ainda passa
    [TestCase(14.9999, SensorStatus.Warning)]
    [TestCase(10.0001, SensorStatus.Warning)]
    [TestCase(10, SensorStatus.Warning)]      // igual ao crítico ainda é atenção (comparação estrita)
    [TestCase(9.9999, SensorStatus.Failing)]
    [TestCase(0.1, SensorStatus.Failing)]
    public void DiskSpace_FreePercent_RespectsFloatBoundaries(double freePercent, SensorStatus expected)
    {
        var result = SensorEvaluator.Evaluate(
            Disk(), DiskMeasure(freePercent), error: null, declaredStatus: null, declaredMessage: null, providedChannels: null);

        Assert.That(result.Status, Is.EqualTo(expected), $"free={freePercent}%");
        Assert.That(result.PrimaryChannel, Is.EqualTo("free_percent"));
        Assert.That(result.PrimaryValue, Is.EqualTo(freePercent).Within(0.0001));
    }

    [Test]
    public void DiskSpace_MissingVolume_IsFailingCriticalWithExplicitMessage()
    {
        var result = SensorEvaluator.Evaluate(
            Disk(), new AgentSensorMeasureDto { FreePercent = null, TotalBytes = 0 },
            null, null, null, null);

        Assert.That(result.Status, Is.EqualTo(SensorStatus.Failing));
        Assert.That(result.Severity, Is.EqualTo(SensorSeverity.Critical));
        Assert.That(result.Message, Does.Contain("não encontrado"));
    }

    [TestCase(85, SensorStatus.Passing)]
    [TestCase(85.01, SensorStatus.Warning)]
    [TestCase(95, SensorStatus.Warning)]
    [TestCase(95.01, SensorStatus.Failing)]
    public void Cpu_UsedPercent_HigherIsWorse(double used, SensorStatus expected)
    {
        var definition = new SensorDefinition
        {
            Name = "CPU",
            SensorType = SensorType.Cpu,
            Comparison = SensorComparison.HigherIsWorse,
            WarningThreshold = 85,
            CriticalThreshold = 95
        };

        var result = SensorEvaluator.Evaluate(
            definition, new AgentSensorMeasureDto { UsedPercent = used }, null, null, null, null);

        Assert.That(result.Status, Is.EqualTo(expected));
    }

    [Test]
    public void Ping_WithoutReplies_IsFailing_NeverPassing()
    {
        var definition = new SensorDefinition
        {
            Name = "Ping 8.8.8.8",
            SensorType = SensorType.Ping,
            Target = "8.8.8.8",
            WarningThreshold = 100,
            CriticalThreshold = 300,
            SeverityWhenFailing = SensorSeverity.Critical
        };

        var result = SensorEvaluator.Evaluate(
            definition,
            new AgentSensorMeasureDto { Sent = 3, Replies = 0, LossPercent = 100, AvgRttMs = null },
            null, null, null, null);

        Assert.That(result.Status, Is.EqualTo(SensorStatus.Failing));
        Assert.That(result.Severity, Is.EqualTo(SensorSeverity.Critical));
        Assert.That(result.Channels["loss_percent"], Is.EqualTo(100));
    }

    [Test]
    public void Service_Stopped_IsFailing_AndRunningIsPassing()
    {
        var definition = new SensorDefinition
        {
            Name = "Spooler",
            SensorType = SensorType.Service,
            Target = "Spooler",
            SeverityWhenFailing = SensorSeverity.Warning
        };

        var stopped = SensorEvaluator.Evaluate(
            definition, new AgentSensorMeasureDto { ServiceState = "Stopped" }, null, null, null, null);
        var running = SensorEvaluator.Evaluate(
            definition, new AgentSensorMeasureDto { ServiceState = "Running" }, null, null, null, null);

        Assert.That(stopped.Status, Is.EqualTo(SensorStatus.Failing));
        Assert.That(stopped.Severity, Is.EqualTo(SensorSeverity.Warning));
        Assert.That(running.Status, Is.EqualTo(SensorStatus.Passing));
        Assert.That(running.Severity, Is.Null);
    }

    private static SensorDefinition Script() => new()
    {
        Name = "Pacotes pendentes",
        SensorType = SensorType.Script,
        ChannelsJson = "[{\"key\":\"pending_count\",\"unit\":\"Count\",\"isPrimary\":true}]",
        ExitCodeMapJson = "[{\"codes\":[0],\"status\":\"Passing\"}," +
                          "{\"codes\":[1],\"status\":\"Failing\",\"severity\":\"Critical\"}," +
                          "{\"codes\":[2],\"status\":\"Warning\",\"severity\":\"Warning\"}," +
                          "{\"codes\":[20],\"status\":\"Unknown\"}]",
        SeverityWhenFailing = SensorSeverity.Critical
    };

    [TestCase(0, SensorStatus.Passing)]
    [TestCase(1, SensorStatus.Failing)]
    [TestCase(2, SensorStatus.Warning)]
    [TestCase(20, SensorStatus.Unknown)]
    [TestCase(5, SensorStatus.Failing)] // não listado != 0 => crítico (failOnUnlistedNonZero default)
    public void Script_ExitCodes_FollowConfiguredMap(int exitCode, SensorStatus expected)
    {
        var result = SensorEvaluator.Evaluate(
            Script(),
            new AgentSensorMeasureDto { ExitCode = exitCode, Stdout = "linha de saida" },
            null, null, null,
            new Dictionary<string, double> { ["pending_count"] = 3 });

        Assert.That(result.Status, Is.EqualTo(expected), $"exit={exitCode}");
    }

    [Test]
    public void Script_DeclaredStatus_WinsOverExitCode()
    {
        var result = SensorEvaluator.Evaluate(
            Script(),
            new AgentSensorMeasureDto { ExitCode = 0 },
            null,
            declaredStatus: "CRITICAL",
            declaredMessage: "banco de dados indisponível",
            providedChannels: null);

        Assert.That(result.Status, Is.EqualTo(SensorStatus.Failing));
        Assert.That(result.Severity, Is.EqualTo(SensorSeverity.Critical));
        Assert.That(result.Message, Is.EqualTo("banco de dados indisponível"));
    }

    [Test]
    public void Script_DiscardsUndeclaredChannels()
    {
        var result = SensorEvaluator.Evaluate(
            Script(),
            new AgentSensorMeasureDto { ExitCode = 0 },
            null, null, null,
            new Dictionary<string, double> { ["pending_count"] = 7, ["canal_inventado"] = 42 });

        Assert.That(result.Channels.Keys, Is.EquivalentTo(new[] { "pending_count" }));
        Assert.That(result.PrimaryChannel, Is.EqualTo("pending_count"));
        Assert.That(result.PrimaryValue, Is.EqualTo(7));
    }

    [Test]
    public void DiskSpace_MultiVolumeMessage_IncludesTheVolume()
    {
        var definition = Disk();
        definition.Name = "Disco";
        definition.Target = null; // todos os volumes: o alvo vem do targetKey

        var result = SensorEvaluator.Evaluate(
            definition, DiskMeasure(8.4), error: null, declaredStatus: null,
            declaredMessage: null, providedChannels: null, targetKey: "D:");

        Assert.That(result.Status, Is.EqualTo(SensorStatus.Failing));
        Assert.That(result.Message, Does.Contain("D:"), "sem o volume as linhas do sensor multi-volume ficam indistinguíveis");
    }

    // ── Fase 5: novos tipos ────────────────────────────────────────────────

    private static SensorDefinition Def(SensorType type, double? warning = null, double? critical = null)
        => new()
        {
            Name = type.ToString(),
            SensorType = type,
            Comparison = type is SensorType.DiskSpace or SensorType.Uptime or SensorType.CertificateExpiry
                ? SensorComparison.LowerIsWorse
                : SensorComparison.HigherIsWorse,
            WarningThreshold = warning,
            CriticalThreshold = critical,
            SeverityWhenFailing = SensorSeverity.Critical
        };

    private static SensorEvaluation Eval(SensorDefinition definition, AgentSensorMeasureDto measure)
        => SensorEvaluator.Evaluate(definition, measure, null, null, null, null);

    /// <summary>
    /// Regressão: sensores por limiar que NÃO mediram nada reportavam Passing
    /// (verde), porque <c>ByThresholds(null)</c> devolvia Passing. Um sensor que
    /// não conseguiu medir não pode aparecer saudável.
    /// </summary>
    [TestCase(SensorType.Bandwidth)]
    [TestCase(SensorType.DiskIo)]
    [TestCase(SensorType.DnsLatency)]
    [TestCase(SensorType.HttpThroughput)]
    [TestCase(SensorType.Ping)]
    [TestCase(SensorType.Cpu)]
    public void MissingMeasurement_IsUnknown_NotPassing(SensorType type)
    {
        // Medição vazia: nenhum canal primário presente.
        var result = Eval(Def(type, warning: 90, critical: 99), new AgentSensorMeasureDto());

        Assert.That(result.Status, Is.EqualTo(SensorStatus.Unknown),
            $"{type} sem medição deve ser Unknown, nunca Passing");
        Assert.That(result.PrimaryValue, Is.Null, "sem medição não há valor primário");
        Assert.That(result.Message, Does.Contain("sem medição"));
    }

    [Test]
    public void EventLogRate_MissingCount_IsUnknown()
    {
        var result = Eval(Def(SensorType.EventLogRate, 50, 200), new AgentSensorMeasureDto());

        Assert.That(result.Status, Is.EqualTo(SensorStatus.Unknown));
        Assert.That(result.Message, Does.Contain("indisponível"));
    }

    [Test]
    public void EventLogRate_ZeroEvents_IsMeasuredAndPasses()
    {
        // Zero medido é um resultado legítimo: continua passando pelos limiares.
        var result = Eval(Def(SensorType.EventLogRate, 50, 200),
            new AgentSensorMeasureDto { EventCount = 0, EventWindowMinutes = 60 });

        Assert.That(result.Status, Is.EqualTo(SensorStatus.Passing));
        Assert.That(result.PrimaryValue, Is.EqualTo(0d));
    }

    [TestCase(5, SensorStatus.Passing)]
    [TestCase(51, SensorStatus.Warning)]
    [TestCase(201, SensorStatus.Failing)]
    public void EventLogRate_UsesEventCountThresholds(int count, SensorStatus expected)
    {
        var result = Eval(Def(SensorType.EventLogRate, 50, 200),
            new AgentSensorMeasureDto { EventCount = count, EventWindowMinutes = 60 });

        Assert.That(result.Status, Is.EqualTo(expected));
        Assert.That(result.Message, Does.Contain("min."));
    }

    [TestCase("Healthy", SensorStatus.Passing)]
    [TestCase("Warning", SensorStatus.Warning)]
    [TestCase("Unhealthy", SensorStatus.Failing)]
    [TestCase("", SensorStatus.Unknown)]
    [TestCase(null, SensorStatus.Unknown)]
    public void SmartStatus_MapsHealthString(string? smart, SensorStatus expected)
    {
        var result = Eval(Def(SensorType.SmartStatus), new AgentSensorMeasureDto { SmartStatus = smart });
        Assert.That(result.Status, Is.EqualTo(expected));
    }

    [Test]
    public void SmartStatus_TemperatureWorsensState()
    {
        var result = Eval(Def(SensorType.SmartStatus, warning: 50, critical: 60),
            new AgentSensorMeasureDto { SmartStatus = "Healthy", TemperatureC = 65 });

        Assert.That(result.Status, Is.EqualTo(SensorStatus.Failing),
            "temperatura acima do crítico deve piorar o resultado, mesmo com SMART Healthy");
    }

    [TestCase(30, SensorStatus.Passing)]
    [TestCase(20, SensorStatus.Warning)]
    [TestCase(5, SensorStatus.Failing)]
    public void CertificateExpiry_LowerIsWorse(int days, SensorStatus expected)
    {
        var result = Eval(Def(SensorType.CertificateExpiry, 30, 15),
            new AgentSensorMeasureDto { DaysToExpiry = days, CertificateSubject = "CN=api" });

        Assert.That(result.Status, Is.EqualTo(expected));
        Assert.That(result.Message, Does.Contain("CN=api"));
    }

    [Test]
    public void Defender_DisabledIsFailing_AndSignatureAgeWorsens()
    {
        var disabled = Eval(Def(SensorType.DefenderStatus),
            new AgentSensorMeasureDto { DefenderEnabled = false, DefenderRealTimeEnabled = true });
        Assert.That(disabled.Status, Is.EqualTo(SensorStatus.Failing));

        var staleSignature = Eval(Def(SensorType.DefenderStatus, warning: 3, critical: 7),
            new AgentSensorMeasureDto { DefenderEnabled = true, DefenderRealTimeEnabled = true, SignatureAgeDays = 10 });
        Assert.That(staleSignature.Status, Is.EqualTo(SensorStatus.Failing));
        Assert.That(staleSignature.Message, Does.Contain("assinatura"));

        var unknown = Eval(Def(SensorType.DefenderStatus), new AgentSensorMeasureDto());
        Assert.That(unknown.Status, Is.EqualTo(SensorStatus.Unknown));
    }

    [TestCase("On", SensorStatus.Passing)]
    [TestCase("Suspended", SensorStatus.Warning)]
    [TestCase("Off", SensorStatus.Failing)]
    [TestCase("", SensorStatus.Unknown)]
    public void BitLocker_MapsProtectionState(string state, SensorStatus expected)
    {
        var result = Eval(Def(SensorType.BitLocker), new AgentSensorMeasureDto { BitLockerStatus = state });
        Assert.That(result.Status, Is.EqualTo(expected));
    }

    [Test]
    public void Service_RestartAttemptAppearsInMessage()
    {
        var definition = Def(SensorType.Service);
        definition.Target = "Spooler";
        definition.SeverityWhenFailing = SensorSeverity.Warning;

        var failed = Eval(definition, new AgentSensorMeasureDto
        {
            ServiceState = "Stopped", RestartAttempted = true, RestartSucceeded = false
        });
        Assert.That(failed.Status, Is.EqualTo(SensorStatus.Failing));
        Assert.That(failed.Message, Does.Contain("tentativa de reinício falhou"));

        var restarted = Eval(definition, new AgentSensorMeasureDto
        {
            ServiceState = "Running", RestartAttempted = true, RestartSucceeded = true
        });
        Assert.That(restarted.Status, Is.EqualTo(SensorStatus.Passing));
        Assert.That(restarted.Message, Does.Contain("reinício automático aplicado"));
    }

    [Test]
    public void SustainSamples_HoldsEscalation_ButNotFirstFailureOrRecovery()
    {
        var definition = Def(SensorType.Cpu, 85, 95);
        definition.SustainSamples = 3;

        // Já em Warning, subindo para Failing: precisa de 3 amostras ruins.
        Assert.That(SensorEvaluator.ShouldHoldEscalation(
            definition, SensorStatus.Warning, SensorStatus.Failing, consecutiveFailuresBefore: 0), Is.True);
        Assert.That(SensorEvaluator.ShouldHoldEscalation(
            definition, SensorStatus.Warning, SensorStatus.Failing, consecutiveFailuresBefore: 2), Is.False);

        // Primeira falha (antes Passing) muda na hora.
        Assert.That(SensorEvaluator.ShouldHoldEscalation(
            definition, SensorStatus.Passing, SensorStatus.Failing, consecutiveFailuresBefore: 0), Is.False);

        // Recuperação (Passing) nunca é segurada.
        Assert.That(SensorEvaluator.ShouldHoldEscalation(
            definition, SensorStatus.Failing, SensorStatus.Passing, consecutiveFailuresBefore: 5), Is.False);

        // Default (1) não segura nada.
        var immediate = Def(SensorType.Cpu, 85, 95);
        Assert.That(SensorEvaluator.ShouldHoldEscalation(
            immediate, SensorStatus.Warning, SensorStatus.Failing, consecutiveFailuresBefore: 0), Is.False);
    }

    [Test]
    public void Error_IsUnknown_AndDoesNotBecomeFailing()
    {
        var result = SensorEvaluator.Evaluate(
            Disk(), new AgentSensorMeasureDto(), error: "acesso negado ao volume",
            declaredStatus: null, declaredMessage: null, providedChannels: null);

        Assert.That(result.Status, Is.EqualTo(SensorStatus.Unknown));
        Assert.That(result.Severity, Is.Null);
        Assert.That(result.Message, Is.EqualTo("acesso negado ao volume"));
    }

    [Test]
    public void Timeout_IsFailingCritical()
    {
        var result = SensorEvaluator.Evaluate(
            Script(), new AgentSensorMeasureDto { TimedOut = true }, null, null, null, null);

        Assert.That(result.Status, Is.EqualTo(SensorStatus.Failing));
        Assert.That(result.Severity, Is.EqualTo(SensorSeverity.Critical));
        Assert.That(result.Message, Does.Contain("Tempo limite"));
    }
}
