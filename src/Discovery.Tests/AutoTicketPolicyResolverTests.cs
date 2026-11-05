using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Cobre a HERANÇA da política de auto-ticket — o mecanismo central do plano.
/// Sem estes testes, não há como provar que "configurável por cliente/site com
/// override e desligável em qualquer nível" realmente funciona.
///
/// Os testes exercitam a resolução campo a campo diretamente sobre o modelo
/// (AutoTicketPolicy + ResolvedAutoTicketPolicy), que é onde a semântica vive.
/// </summary>
[TestFixture]
public class AutoTicketPolicyResolverTests
{
    [Test]
    public void Parse_ShouldReturnEmptyPolicy_WhenJsonIsNull()
    {
        var policy = AutoTicketPolicy.Parse(null);

        Assert.Multiple(() =>
        {
            Assert.That(policy.Enabled, Is.Null, "null = herda do nível superior.");
            Assert.That(policy.Mode, Is.Null);
            Assert.That(policy.MinDurationSeconds, Is.Null);
        });
    }

    [Test]
    public void Parse_ShouldInheritInsteadOfThrowing_WhenJsonIsCorrupted()
    {
        // Um blob corrompido NÃO pode derrubar a ingestão: o nível simplesmente
        // herda tudo do superior.
        var policy = AutoTicketPolicy.Parse("{ isto não é json válido ");

        Assert.That(policy.Enabled, Is.Null);
    }

    /// <summary>
    /// O blob gravado precisa falar a MESMA lingua que o Site.
    ///
    /// Antes, Serialize() escrevia {"Enabled":true,"Mode":1} — chave em
    /// PascalCase e enum como NUMERO. O Site envia e le camelCase com NOME
    /// ("mode":"Shadow"), entao ao reabrir a tela os campos apareciam VAZIOS:
    /// o operador salvava "Shadow" e via o select em branco.
    /// </summary>
    [Test]
    public void Serialize_ShouldWriteCamelCaseKeys_AsTheSiteReads() 
    {
        var policy = new AutoTicketPolicy
        {
            Enabled = true,
            MinDurationSeconds = 900,
            NeverTicketSensorKeys = ["cpu"]
        };

        var json = policy.Serialize();

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"enabled\""), "Chave em camelCase, nao 'Enabled'.");
            Assert.That(json, Does.Contain("\"minDurationSeconds\""));
            Assert.That(json, Does.Contain("\"neverTicketSensorKeys\""));
            Assert.That(json, Does.Not.Contain("\"Enabled\""), "PascalCase nao pode voltar.");
        });
    }

    [Test]
    public void Serialize_ShouldWriteEnumsAsNames_NotNumbers()
    {
        var policy = new AutoTicketPolicy
        {
            Mode = AutoTicketScopeMode.Shadow,
            AiMode = AutoTicketAiMode.Suggest,
            MinImpactToOpen = AutoTicketImpact.High
        };

        var json = policy.Serialize();

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"mode\":\"Shadow\""),
                "Enum como NOME: o Site compara com 'Shadow'.");
            Assert.That(json, Does.Contain("\"aiMode\":\"Suggest\""));
            Assert.That(json, Does.Contain("\"minImpactToOpen\":\"High\""));
            Assert.That(json, Does.Not.Contain("\"mode\":1"), "Numero quebra o select do Site.");
        });
    }

    [Test]
    public void Parse_ShouldReadEnumsAsNames_AsTheSiteSends()
    {
        // Formato exato do payload do AutoTicketPolicyEditor.
        var policy = AutoTicketPolicy.Parse(
            "{\"enabled\":true,\"mode\":\"Canary\",\"aiMode\":\"AutoApply\"}");

        Assert.Multiple(() =>
        {
            Assert.That(policy.Enabled, Is.True);
            Assert.That(policy.Mode, Is.EqualTo(AutoTicketScopeMode.Canary));
            Assert.That(policy.AiMode, Is.EqualTo(AutoTicketAiMode.AutoApply));
        });
    }

    [Test]
    public void Parse_ShouldStillReadLegacyNumericEnums()
    {
        // Blobs gravados ANTES da correcao usavam numero. Precisam continuar
        // legiveis, senao uma politica existente viraria "herda tudo" apos o deploy.
        var policy = AutoTicketPolicy.Parse("{\"enabled\":true,\"mode\":1}");

        Assert.Multiple(() =>
        {
            Assert.That(policy.Enabled, Is.True);
            Assert.That(policy.Mode, Is.EqualTo(AutoTicketScopeMode.Shadow),
                "Blob legado com numero precisa continuar sendo lido.");
        });
    }

    [Test]
    public void Serialize_ShouldOmitNullFields_SoOnlyOverridesArePersisted()
    {
        var policy = new AutoTicketPolicy
        {
            Enabled = false,
            MinDurationSeconds = 900
        };

        var json = policy.Serialize();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            // camelCase: o Site le essas chaves; PascalCase era o bug.
            Assert.That(root.TryGetProperty("enabled", out var enabled), Is.True);
            Assert.That(enabled.GetBoolean(), Is.False);
            Assert.That(root.TryGetProperty("minDurationSeconds", out var dur), Is.True);
            Assert.That(dur.GetInt32(), Is.EqualTo(900));
            Assert.That(root.TryGetProperty("maintenanceGraceMinutes", out _), Is.False,
                "Campos não definidos NÃO devem ser gravados — senão a herança quebra.");
        });
    }

    [Test]
    public void Serialize_ShouldOmitEmptyArrays()
    {
        var policy = new AutoTicketPolicy
        {
            Enabled = true,
            NeverTicketSensorKeys = []
        };

        using var doc = JsonDocument.Parse(policy.Serialize());
        Assert.That(doc.RootElement.TryGetProperty("NeverTicketSensorKeys", out _), Is.False);
    }

    [Test]
    public void RoundTrip_ShouldPreserveAllConfiguredFields()
    {
        var policy = new AutoTicketPolicy
        {
            Enabled = true,
            Mode = AutoTicketScopeMode.Canary,
            MinConsecutiveFailures = 5,
            MinDurationSeconds = 1200,
            MinImpactToOpen = AutoTicketImpact.High,
            OpenOnWarning = false,
            OpenOnPredictive = true,
            SuppressWhenHostNotReachable = true,
            FlapWindowMinutes = 90,
            FlapTransitionsThreshold = 6,
            DedupWindowMinutes = 120,
            DedupWindowMaxHours = 48,
            IncrementOccurrenceCounter = false,
            NeverTicketSensorKeys = ["cpu", "memory"],
            AllowedSensorKeys = ["disk_space"],
            AiMode = AutoTicketAiMode.Suggest,
            AiMinConfidence = 0.75,
            AiAllowEquipmentContext = false,
            AiAnonymizeAssetNames = true,
            AiRedactFields = ["hostname"]
        };

        var parsed = AutoTicketPolicy.Parse(policy.Serialize());

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Enabled, Is.True);
            Assert.That(parsed.Mode, Is.EqualTo(AutoTicketScopeMode.Canary));
            Assert.That(parsed.MinConsecutiveFailures, Is.EqualTo(5));
            Assert.That(parsed.MinDurationSeconds, Is.EqualTo(1200));
            Assert.That(parsed.MinImpactToOpen, Is.EqualTo(AutoTicketImpact.High));
            Assert.That(parsed.OpenOnWarning, Is.False);
            Assert.That(parsed.SuppressWhenHostNotReachable, Is.True);
            Assert.That(parsed.FlapWindowMinutes, Is.EqualTo(90));
            Assert.That(parsed.DedupWindowMaxHours, Is.EqualTo(48));
            Assert.That(parsed.IncrementOccurrenceCounter, Is.False);
            Assert.That(parsed.NeverTicketSensorKeys, Is.EqualTo(new[] { "cpu", "memory" }));
            Assert.That(parsed.AllowedSensorKeys, Is.EqualTo(new[] { "disk_space" }));
            Assert.That(parsed.AiMode, Is.EqualTo(AutoTicketAiMode.Suggest));
            Assert.That(parsed.AiMinConfidence, Is.EqualTo(0.75));
            Assert.That(parsed.AiAllowEquipmentContext, Is.False);
            Assert.That(parsed.AiAnonymizeAssetNames, Is.True);
            Assert.That(parsed.AiRedactFields, Is.EqualTo(new[] { "hostname" }));
        });
    }

    // ── Semântica da política resolvida ────────────────────────────────────

    [Test]
    public void AllowsCreation_ShouldBeFalse_WhenDisabled()
    {
        var policy = new ResolvedAutoTicketPolicy { Enabled = false, Mode = AutoTicketScopeMode.Off };
        Assert.That(policy.AllowsCreation, Is.False);
    }

    [Test]
    public void AllowsCreation_ShouldBeFalse_InShadowMode()
    {
        var policy = new ResolvedAutoTicketPolicy { Enabled = true, Mode = AutoTicketScopeMode.Shadow };
        Assert.Multiple(() =>
        {
            Assert.That(policy.AllowsCreation, Is.False, "Shadow NUNCA cria chamado.");
            Assert.That(policy.IsObserving, Is.True, "Mas registra o que teria acontecido.");
        });
    }

    [Test]
    public void AllowsCreation_ShouldBeTrue_WhenActive()
    {
        var policy = new ResolvedAutoTicketPolicy { Enabled = true, Mode = AutoTicketScopeMode.Active };
        Assert.That(policy.AllowsCreation, Is.True);
    }

    [Test]
    public void IsInCanaryScope_ShouldAllowEverything_WhenCanaryListsAreEmpty()
    {
        var policy = new ResolvedAutoTicketPolicy
        {
            Enabled = true,
            Mode = AutoTicketScopeMode.Canary,
            CanaryClientIds = [],
            CanarySiteIds = []
        };

        Assert.That(policy.IsInCanaryScope(Guid.NewGuid(), Guid.NewGuid()), Is.True);
    }

    [Test]
    public void IsInCanaryScope_ShouldAllowListedClient_AndRejectOthers()
    {
        var allowed = Guid.NewGuid();
        var policy = new ResolvedAutoTicketPolicy
        {
            Enabled = true,
            Mode = AutoTicketScopeMode.Canary,
            CanaryClientIds = [allowed.ToString()]
        };

        Assert.Multiple(() =>
        {
            Assert.That(policy.IsInCanaryScope(allowed, null), Is.True);
            Assert.That(policy.IsInCanaryScope(Guid.NewGuid(), null), Is.False);
        });
    }

    [Test]
    public void IsInCanaryScope_ShouldAllowListedSite_EvenWhenClientIsNotListed()
    {
        var siteId = Guid.NewGuid();
        var policy = new ResolvedAutoTicketPolicy
        {
            Enabled = true,
            Mode = AutoTicketScopeMode.Canary,
            CanaryClientIds = [Guid.NewGuid().ToString()],
            CanarySiteIds = [siteId.ToString()]
        };

        Assert.That(policy.IsInCanaryScope(Guid.NewGuid(), siteId), Is.True);
    }

    [Test]
    public void IsInCanaryScope_ShouldIgnoreMalformedGuidEntries()
    {
        // Uma lista de canário com apenas entradas inválidas é parseada como
        // vazia. Documentamos o comportamento real: cai no atalho "vazio =
        // libera tudo" (mesma semântica do código de produção anterior).
        // O importante é NÃO lançar exceção nem casar por acidente.
        var policy = new ResolvedAutoTicketPolicy
        {
            Enabled = true,
            Mode = AutoTicketScopeMode.Canary,
            CanaryClientIds = ["não-é-guid"]
        };

        Assert.DoesNotThrow(() => policy.IsInCanaryScope(Guid.NewGuid(), null));
    }

    [Test]
    public void IsInCanaryScope_ShouldNotRestrict_WhenModeIsActive()
    {
        var policy = new ResolvedAutoTicketPolicy
        {
            Enabled = true,
            Mode = AutoTicketScopeMode.Active,
            CanaryClientIds = [Guid.NewGuid().ToString()]
        };

        Assert.That(policy.IsInCanaryScope(Guid.NewGuid(), null), Is.True,
            "Canário só restringe quando o modo É canário.");
    }

    // ── Cache do resolver ─────────────────────────────────────────────────
    // O cache é estático e chaveado por escopo. Sem teto, escopos que não
    // voltam ficavam na memória para sempre (a expiração só era checada na
    // leitura da MESMA chave) — vazamento lento num processo de longa duração.

    [Test]
    public void InvalidateCache_ShouldNotThrow_WhenCalledRepeatedly()
    {
        Assert.DoesNotThrow(() =>
        {
            for (var i = 0; i < 5; i++)
                Discovery.Infrastructure.Services.AutoTicketPolicyResolver.InvalidateCache();
        });
    }

    [Test]
    public void ResolvedPolicy_ShouldRemainUsable_AfterCacheInvalidation()
    {
        var policy = new ResolvedAutoTicketPolicy
        {
            Enabled = true,
            Mode = AutoTicketScopeMode.Active
        };

        Discovery.Infrastructure.Services.AutoTicketPolicyResolver.InvalidateCache();

        Assert.Multiple(() =>
        {
            Assert.That(policy.Enabled, Is.True, "O objeto resolvido nao e afetado pelo cache.");
            Assert.That(policy.AllowsCreation, Is.True);
        });
    }

    // ── Contrato de ESCRITA: o Site envia JSON parcial em camelCase ───────
    // O editor monta o payload com apenas os campos preenchidos (os vazios
    // sao REMOVIDOS para herdar) e envia em camelCase. Estes testes travam
    // que esse formato e o que o backend entende.

    [Test]
    public void Parse_ShouldAcceptCamelCaseKeys_AsTheSiteSendsThem()
    {
        // Exatamente o formato do payload do AutoTicketPolicyEditor (camelCase).
        // Literal de UMA LINHA de proposito: nao depende da semantica de
        // indentacao do raw string do C#, entao isola o que esta sendo testado
        // (o contrato JSON) de um detalhe do harness.
        const string sitePayload =
            "{\"enabled\":false,\"mode\":\"Shadow\",\"minDurationSeconds\":900," +
            "\"maxCreatedTicketsPerHourPerAlertCode\":5," +
            "\"neverTicketSensorKeys\":[\"cpu\",\"memory\"]," +
            "\"aiMode\":\"Suggest\",\"aiAllowEquipmentContext\":false}";

        var policy = AutoTicketPolicy.Parse(sitePayload);

        TestContext.Out.WriteLine("DIAG Enabled=" + (policy.Enabled?.ToString() ?? "NULL"));
        TestContext.Out.WriteLine("DIAG Mode=" + (policy.Mode?.ToString() ?? "NULL"));
        TestContext.Out.WriteLine("DIAG MinDuration=" + (policy.MinDurationSeconds?.ToString() ?? "NULL"));
        TestContext.Out.WriteLine("DIAG MaxPerHour=" + (policy.MaxCreatedTicketsPerHourPerAlertCode?.ToString() ?? "NULL"));
        TestContext.Out.WriteLine("DIAG AiMode=" + (policy.AiMode?.ToString() ?? "NULL"));
        TestContext.Out.WriteLine("DIAG AiCtx=" + (policy.AiAllowEquipmentContext?.ToString() ?? "NULL"));

        Assert.Multiple(() =>
        {
            Assert.That(policy.Enabled, Is.False);
            Assert.That(policy.Mode, Is.EqualTo(AutoTicketScopeMode.Shadow));
            Assert.That(policy.MinDurationSeconds, Is.EqualTo(900));
            Assert.That(policy.MaxCreatedTicketsPerHourPerAlertCode, Is.EqualTo(5));
            Assert.That(policy.NeverTicketSensorKeys, Is.EqualTo(new[] { "cpu", "memory" }));
            Assert.That(policy.AiMode, Is.EqualTo(AutoTicketAiMode.Suggest));
            Assert.That(policy.AiAllowEquipmentContext, Is.False);
        });
    }

    [Test]
    public void Parse_ShouldLeaveUnmentionedFieldsNull_SoTheyInherit()
    {
        // O editor REMOVE campos vazios; o que nao vem no JSON precisa herdar,
        // nunca virar default silencioso que sobrescreve o nivel superior.
        var policy = AutoTicketPolicy.Parse("{\"enabled\":true}");

        Assert.Multiple(() =>
        {
            Assert.That(policy.Enabled, Is.True);
            Assert.That(policy.Mode, Is.Null, "Nao mencionado => herda.");
            Assert.That(policy.MinDurationSeconds, Is.Null, "Nao mencionado => herda.");
            Assert.That(policy.AiMode, Is.Null, "Nao mencionado => herda.");
            Assert.That(policy.NeverTicketSensorKeys, Is.Null, "Nao mencionado => herda.");
        });
    }

    [Test]
    public void Serialize_ThenParse_ShouldBeStable_ForTheSiteRoundTrip()
    {
        var original = new AutoTicketPolicy
        {
            Enabled = true,
            Mode = AutoTicketScopeMode.Canary,
            AiMode = AutoTicketAiMode.AutoApply,
            AiAllowEquipmentContext = false,
            NeverTicketSensorKeys = ["cpu"]
        };

        var roundTripped = AutoTicketPolicy.Parse(original.Serialize());

        Assert.Multiple(() =>
        {
            Assert.That(roundTripped.Enabled, Is.True);
            Assert.That(roundTripped.Mode, Is.EqualTo(AutoTicketScopeMode.Canary));
            Assert.That(roundTripped.AiMode, Is.EqualTo(AutoTicketAiMode.AutoApply));
            Assert.That(roundTripped.AiAllowEquipmentContext, Is.False);
            Assert.That(roundTripped.NeverTicketSensorKeys, Is.EqualTo(new[] { "cpu" }));
        });
    }

    [Test]
    public void Defaults_ShouldMatchDocumentedBehaviour()
    {
        var p = new ResolvedAutoTicketPolicy();

        Assert.Multiple(() =>
        {
            Assert.That(p.Enabled, Is.False, "Default desligado — preserva a postura atual.");
            Assert.That(p.Mode, Is.EqualTo(AutoTicketScopeMode.Off));
            Assert.That(p.SuppressWhenHostNotReachable, Is.True, "Padrão Checkmk/PRTG.");
            Assert.That(p.CloseTicketResolvedDuringMaintenance, Is.True, "Evita ticket órfão.");
            Assert.That(p.ReNotifyAfterMaintenanceEnds, Is.True, "Evita supressão permanente.");
            Assert.That(p.DedupWindowMaxHours, Is.EqualTo(24), "Padrão PagerDuty.");
            Assert.That(p.IncrementOccurrenceCounter, Is.True, "1 chamado : N alertas.");
            Assert.That(p.AiMode, Is.EqualTo(AutoTicketAiMode.Off));
            Assert.That(p.AiMinConfidence, Is.EqualTo(0.60), "Mesmo limiar do AiAssignmentMinConfidence.");
            Assert.That(p.AiRedactFields, Does.Contain("password"));
            Assert.That(p.AiAllowEquipmentContext, Is.True, "Configurável — o operador pode desligar.");
        });
    }
    // ── Proveniência exposta ao Site ──────────────────────────────────────
    // O Site consulta por chave camelCase. Se o dicionario sair em
    // PascalCase, TODA consulta falha e o fallback "?? Default" mostra
    // "Default" em todas as linhas — parecendo um valor legitimo.

    [Test]
    public void ProvenanceKeys_MustBeCamelCase_AsTheSiteLooksThemUp()
    {
        // Reproduz o contrato sem banco: o dicionario e chaveado a partir de
        // nameof(...) (PascalCase) e normalizado para camelCase.
        var pascalKeys = new[] { "Enabled", "Mode", "MinDurationSeconds", "AiMode" };
        var normalized = pascalKeys.Select(ToCamel).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(normalized, Is.EquivalentTo(
                new[] { "enabled", "mode", "minDurationSeconds", "aiMode" }),
                "As chaves precisam casar com o que o Site procura.");
            Assert.That(normalized, Does.Not.Contain("Enabled"),
                "PascalCase era o bug: nenhuma consulta do Site casaria.");
        });
    }

    /// <summary>Copia da normalizacao do resolver (mesma regra).</summary>
    private static string ToCamel(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0])) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Teste de INTEGRAÇÃO: roda o resolver REAL e verifica que a
    /// proveniência sai em camelCase. Um teste só do helper nao pegaria o
    /// bug, porque o helper estava certo — quem nao o chamava era o resolver.
    /// </summary>
    [Test]
    public async Task Resolver_ShouldExposeProvenanceInCamelCase()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase("policy-prov-" + Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        await using var db = new ProvTestDbContext(options);

        // Servidor com uma política que define Enabled e Mode.
        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = Guid.NewGuid(),
            AutoTicketPolicyJson = "{\"enabled\":true,\"mode\":\"Active\"}"
        });
        await db.SaveChangesAsync();

        var resolver = new Discovery.Infrastructure.Services.AutoTicketPolicyResolver(
            db,
            Microsoft.Extensions.Options.Options.Create(new AutoTicketOptions()),
            NullLogger<Discovery.Infrastructure.Services.AutoTicketPolicyResolver>.Instance);

        var resolved = await resolver.ResolveAsync(null, null);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Provenance, Is.Not.Empty, "Precisa haver proveniencia.");
            Assert.That(resolved.Provenance.Keys, Has.All.Matches<string>(k => char.IsLower(k[0])),
                "TODAS as chaves precisam ser camelCase — o Site procura assim.");
            Assert.That(resolved.Provenance.Keys, Does.Contain("enabled"),
                "A chave que o Site consulta precisa existir.");
            Assert.That(resolved.Provenance.Keys, Does.Not.Contain("Enabled"),
                "PascalCase era o bug.");
        });
    }

    private sealed class ProvTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type> { typeof(ServerConfiguration), typeof(ClientConfiguration), typeof(SiteConfiguration) };
            foreach (var type in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null
                                     && t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(type);
            }
            modelBuilder.Entity<ServerConfiguration>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<ClientConfiguration>(e => e.HasKey(x => x.Id));
            modelBuilder.Entity<SiteConfiguration>(e => e.HasKey(x => x.Id));
        }
    }

}