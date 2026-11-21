using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Discovery.Tests;

/// <summary>
/// Herança da política de WebRTC do acesso remoto (servidor → cliente → site).
///
/// O contrato testado, espelhando RemoteProxyPolicyInheritanceTests:
///   1. o escopo mais específico que DEFINIR o campo substitui o de cima POR
///      INTEIRO — não há merge de listas de STUN/TURN nem união de kinds;
///   2. ausente em todos, o WebRTC fica DESLIGADO (o comportamento anterior,
///      todo o tráfego pelo NATS) — nunca ligado;
///   3. o kill switch booleano é resolvido SEPARADAMENTE do blob, então desligar
///      num site não apaga a configuração de STUN/TURN que ele já tinha;
///   4. efetivo = blob.Enabled && flag — fail-closed duplo;
///   5. os defaults de STUN são os públicos gratuitos (Google + Cloudflare).
/// </summary>
[TestFixture]
public class RemoteAccessWebRtcInheritanceTests
{
    private const string EnabledWithTurn = """
        {"enabled":true,"kinds":["screen"],
         "ice":{"stunUrls":["stun:stun.example.net:3478"],
                "turnUrls":["turn:turn.example.net:3478"],
                "turnUsername":"u","turnCredential":"p"}}
        """;

    private const string EnabledScreenAndRdp = """
        {"enabled":true,"kinds":["screen","rdp"]}
        """;

    private const string EnabledButBlobDisabled = """
        {"enabled":false,"kinds":["screen"]}
        """;

    // ── Precedência ──────────────────────────────────────────────────────

    [Test]
    public async Task Site_vence_cliente_e_servidor()
    {
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: EnabledWithTurn, client: EnabledScreenAndRdp, site: EnabledScreenAndRdp,
            serverEnabled: true, clientEnabled: true, siteEnabled: true);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteAccessWebRtcEnabled, Is.True);
            Assert.That(resolved.RemoteAccessWebRtc.EffectiveKinds,
                Is.EqualTo(new[] { "screen", "rdp" }), "o site define e vence");
            Assert.That(resolved.Inheritance["RemoteAccessWebRtcPolicyJson"],
                Is.EqualTo((int)ConfigurationPriorityType.Site));
        });
    }

    [Test]
    public async Task Cliente_vence_servidor_quando_site_nao_define()
    {
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: EnabledWithTurn, client: EnabledScreenAndRdp, site: null,
            serverEnabled: true, clientEnabled: true, siteEnabled: null);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteAccessWebRtc.EffectiveKinds, Is.EqualTo(new[] { "screen", "rdp" }));
            Assert.That(resolved.Inheritance["RemoteAccessWebRtcPolicyJson"],
                Is.EqualTo((int)ConfigurationPriorityType.Client));
        });
    }

    [Test]
    public async Task Cai_para_o_servidor_quando_cliente_e_site_nao_definem()
    {
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: EnabledWithTurn, client: null, site: null,
            serverEnabled: true, clientEnabled: null, siteEnabled: null);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteAccessWebRtc.EffectiveKinds, Is.EqualTo(new[] { "screen" }));
            Assert.That(resolved.RemoteAccessWebRtc.Ice.StunUrls,
                Is.EqualTo(new[] { "stun:stun.example.net:3478" }));
            Assert.That(resolved.Inheritance["RemoteAccessWebRtcPolicyJson"],
                Is.EqualTo((int)ConfigurationPriorityType.Global));
        });
    }

    [Test]
    public async Task Politica_do_site_substitui_por_inteiro_sem_merge_de_stun()
    {
        // Ponto central do contrato: um site que define a política NÃO herda os
        // servidores STUN do servidor. Merge implícito de listas ICE seria
        // impossível de auditar ("de onde veio este STUN?").
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: EnabledWithTurn, client: null, site: EnabledScreenAndRdp,
            serverEnabled: true, clientEnabled: null, siteEnabled: true);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteAccessWebRtc.Ice.TurnUrls, Is.Empty,
                "o TURN do servidor NÃO vaza para o site que definiu política própria");
            Assert.That(resolved.RemoteAccessWebRtc.EffectiveKinds, Is.EqualTo(new[] { "screen", "rdp" }));
        });
    }

    // ── Kill switch separado do blob ─────────────────────────────────────

    [Test]
    public async Task Kill_switch_desligado_anula_o_blob_mas_preserva_a_configuracao()
    {
        // O valor de ter o booleano SEPARADO: desligar o WebRTC num site não
        // apaga o STUN/TURN que ele já tinha cadastrado.
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: EnabledWithTurn, client: null, site: null,
            serverEnabled: false, clientEnabled: null, siteEnabled: null);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteAccessWebRtcEnabled, Is.False, "flag desligada ⇒ efetivo desligado");
            Assert.That(resolved.RemoteAccessWebRtc.Ice.StunUrls,
                Is.EqualTo(new[] { "stun:stun.example.net:3478" }),
                "a configuração continua legível/visível na UI");
            Assert.That(resolved.RemoteAccessWebRtc.Enabled, Is.True,
                "o blob em si não foi alterado");
        });
    }

    [Test]
    public async Task Blob_desligado_anula_a_flag_ligada()
    {
        // Fail-closed duplo: os DOIS precisam estar ligados.
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: EnabledButBlobDisabled, client: null, site: null,
            serverEnabled: true, clientEnabled: null, siteEnabled: null);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.That(resolved.RemoteAccessWebRtcEnabled, Is.False);
    }

    [Test]
    public async Task Site_pode_desligar_o_que_o_cliente_ligou()
    {
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: EnabledWithTurn, client: EnabledScreenAndRdp, site: EnabledButBlobDisabled,
            serverEnabled: true, clientEnabled: true, siteEnabled: true);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteAccessWebRtcEnabled, Is.False, "o site desliga para si");
            Assert.That(resolved.RemoteAccessWebRtc.Enabled, Is.False);
        });
    }

    [Test]
    public async Task Kill_switch_do_site_vence_quando_cliente_e_servidor_ligaram()
    {
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: EnabledWithTurn, client: EnabledScreenAndRdp, site: null,
            serverEnabled: true, clientEnabled: true, siteEnabled: false);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteAccessWebRtcEnabled, Is.False);
            Assert.That(resolved.Inheritance["RemoteAccessWebRtcEnabled"],
                Is.EqualTo((int)ConfigurationPriorityType.Site));
        });
    }

    // ── Fail-closed ──────────────────────────────────────────────────────

    [Test]
    public async Task Sem_politica_em_nenhum_escopo_o_webrtc_fica_desligado()
    {
        // Default do rollout: nada configurado ⇒ tudo pelo NATS, como antes.
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: null, client: null, site: null,
            serverEnabled: false, clientEnabled: null, siteEnabled: null);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteAccessWebRtcEnabled, Is.False);
            Assert.That(resolved.RemoteAccessWebRtc.IsEffective, Is.False);
        });
    }

    [Test]
    public async Task Politica_corrompida_no_site_cai_em_webrtc_desligado()
    {
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: EnabledWithTurn, client: null, site: "{isso-nao-e-json",
            serverEnabled: true, clientEnabled: null, siteEnabled: true);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteAccessWebRtc.Enabled, Is.False, "JSON corrompido nunca liga o WebRTC");
            Assert.That(resolved.RemoteAccessWebRtcEnabled, Is.False);
        });
    }

    // ── Defaults de STUN (o pedido explícito) ────────────────────────────

    [Test]
    public void Defaults_de_stun_sao_os_publicos_gratuitos()
    {
        var policy = new RemoteAccessWebRtcPolicy();

        Assert.Multiple(() =>
        {
            Assert.That(policy.Ice.StunUrls, Does.Contain("stun:stun.l.google.com:19302"),
                "Google STUN é o default de convergência (MeshCentral e Breeze)");
            Assert.That(policy.Ice.StunUrls, Does.Contain("stun:stun.cloudflare.com:3478"),
                "Cloudflare STUN é o segundo default de convergência");
            Assert.That(policy.Ice.TurnUrls, Is.Empty, "TURN exige infraestrutura do operador");
            Assert.That(policy.Enabled, Is.False, "nasce DESLIGADO: o caminho NATS é o caminho");
        });
    }

    [Test]
    public void Defaults_de_timeout_sao_afinados_para_suporte_remoto()
    {
        // Os defaults do pion (25 s de failed) são de videochamada; suporte
        // remoto precisa detectar a falha e cair para o NATS rápido.
        var policy = new RemoteAccessWebRtcPolicy();

        Assert.Multiple(() =>
        {
            Assert.That(policy.Ice.DisconnectedTimeoutSeconds, Is.EqualTo(5));
            Assert.That(policy.Ice.FailedTimeoutSeconds, Is.EqualTo(15));
            Assert.That(policy.Ice.KeepAliveIntervalSeconds, Is.EqualTo(2));
        });
    }

    [Test]
    public void EffectiveIceServers_omite_turn_sem_url()
    {
        var policy = new RemoteAccessWebRtcPolicy();
        var servers = policy.EffectiveIceServers;

        Assert.Multiple(() =>
        {
            Assert.That(servers, Has.Count.EqualTo(1), "só STUN por default");
            Assert.That(servers[0].Username, Is.Null);
            Assert.That(servers[0].Urls.Any(u => u.StartsWith("turn:")), Is.False);
        });
    }

    [Test]
    public void EffectiveIceServers_inclui_turn_com_credencial()
    {
        var policy = RemoteAccessWebRtcPolicy.Parse(EnabledWithTurn);
        var servers = policy.EffectiveIceServers;

        Assert.Multiple(() =>
        {
            Assert.That(servers, Has.Count.EqualTo(2));
            var turn = servers[1];
            Assert.That(turn.Urls, Is.EqualTo(new[] { "turn:turn.example.net:3478" }));
            Assert.That(turn.Username, Is.EqualTo("u"));
            Assert.That(turn.Credential, Is.EqualTo("p"));
        });
    }

    // ── Kind terminal é recusado (decisão por medição) ───────────────────

    [Test]
    public void Terminal_nao_e_aceito_como_kind()
    {
        // Terminal não ganha com WebRTC: o gargalo é 1 round-trip e o volume é
        // trivial (coalescer de 16 ms). Recusar explicitamente evita que alguém
        // ligue "só por simetria" e crie um caminho de falha sem benefício.
        var json = """{"enabled":true,"kinds":["terminal"]}""";

        Assert.Multiple(() =>
        {
            Assert.That(RemoteAccessWebRtcPolicy.TryParseStrict(json, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("terminal").IgnoreCase.Or.Contain("não é suportado"));
        });
    }

    [Test]
    public void Kind_desconhecido_e_filtrado_na_leitura_tolerante()
    {
        // Leitura tolerante (runtime) não lança: apenas descarta o que não serve.
        var policy = RemoteAccessWebRtcPolicy.Parse("""{"enabled":true,"kinds":["screen","banana"]}""");

        Assert.That(policy.EffectiveKinds, Is.EqualTo(new[] { "screen" }));
    }

    // ── H.264 permanece travado ──────────────────────────────────────────

    [Test]
    public void H264_e_recusado_no_salvamento()
    {
        var json = """{"enabled":true,"h264Enabled":true}""";

        Assert.Multiple(() =>
        {
            Assert.That(RemoteAccessWebRtcPolicy.TryParseStrict(json, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("H.264"));
        });
    }

    [Test]
    public void H264_e_forcado_a_false_mesmo_em_json_adulterado()
    {
        // O sanitizador é a segunda barreira: mesmo que um JSON chegue por outro
        // caminho, o runtime nunca liga o encoder AVC (patente).
        var policy = RemoteAccessWebRtcPolicy.Parse("""{"enabled":true,"h264Enabled":true}""");

        Assert.That(policy.H264Enabled, Is.False);
    }

    // ── relayOnly fail-closed ────────────────────────────────────────────

    [Test]
    public void RelayOnly_sem_turn_e_recusado()
    {
        // Sem esta guarda o operador salvaria e a sessão NUNCA conectaria.
        var json = """{"enabled":true,"ice":{"relayOnly":true,"turnUrls":[]}}""";

        Assert.Multiple(() =>
        {
            Assert.That(RemoteAccessWebRtcPolicy.TryParseStrict(json, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("relayOnly").IgnoreCase);
        });
    }

    [Test]
    public void RelayOnly_com_turn_e_aceito()
    {
        var json = """
            {"enabled":true,"ice":{"relayOnly":true,"turnUrls":["turn:t.example.net:3478"]}}
            """;

        Assert.That(RemoteAccessWebRtcPolicy.TryParseStrict(json, out var policy, out _), Is.True);
        Assert.That(policy.Ice.RelayOnly, Is.True);
    }

    [Test]
    public void Stun_com_esquema_errado_e_recusado()
    {
        var json = """{"enabled":true,"ice":{"stunUrls":["turn:nao-e-stun.net:3478"]}}""";

        Assert.That(RemoteAccessWebRtcPolicy.TryParseStrict(json, out _, out var error), Is.False);
        Assert.That(error, Does.Contain("STUN").IgnoreCase);
    }

    // ── Serialização para o console ──────────────────────────────────────

    [Test]
    public async Task Politica_resolvida_serializa_ice_servers_para_o_console()
    {
        // O console conecta o navegador com { iceServers } e precisa dos campos
        // em camelCase. Se a forma mudar, a negociação ICE quebra em silêncio.
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: null, client: null, site: EnabledWithTurn,
            serverEnabled: false, clientEnabled: null, siteEnabled: true);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        var json = JsonSerializer.Serialize(resolved.RemoteAccessWebRtc,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("enabled").GetBoolean(), Is.True);
            Assert.That(root.TryGetProperty("ice", out var ice), Is.True);

            Assert.That(ice.TryGetProperty("stunUrls", out var stun), Is.True,
                "o console lê stunUrls para montar iceServers");
            Assert.That(stun.GetArrayLength(), Is.EqualTo(1));

            Assert.That(ice.TryGetProperty("turnUrls", out var turn), Is.True);
            Assert.That(turn.GetArrayLength(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task EffectiveIceServers_no_formato_do_wire()
    {
        // O navegador consome 'urls'/'username'/'credential' — é o mesmo formato
        // do Breeze (que aceita urls como string OU array; a API sempre emite array).
        await using var db = CreateDb();
        var siteId = await SeedAsync(db,
            server: null, client: null, site: EnabledWithTurn,
            serverEnabled: false, clientEnabled: null, siteEnabled: true);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);
        var json = JsonSerializer.Serialize(resolved.RemoteAccessWebRtc.EffectiveIceServers,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);

        var turnEntry = doc.RootElement.EnumerateArray()
            .First(e => e.GetProperty("urls").EnumerateArray().Any(u => u.GetString()!.StartsWith("turn:")));

        Assert.Multiple(() =>
        {
            Assert.That(turnEntry.GetProperty("username").GetString(), Is.EqualTo("u"));
            Assert.That(turnEntry.GetProperty("credential").GetString(), Is.EqualTo("p"));
        });
    }

    // ── Catálogo e entidades ─────────────────────────────────────────────

    [Test]
    public void Campos_estao_no_catalogo_de_campos_gerenciados()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ConfigurationFieldCatalog.ManagedFields, Does.Contain("RemoteAccessWebRtcPolicyJson"));
            Assert.That(ConfigurationFieldCatalog.ManagedFields, Does.Contain("RemoteAccessWebRtcEnabled"));
        });
    }

    [Test]
    public void Entidades_declaram_os_campos()
    {
        Assert.Multiple(() =>
        {
            Assert.That(typeof(ServerConfiguration).GetProperty("RemoteAccessWebRtcPolicyJson"), Is.Not.Null);
            Assert.That(typeof(ServerConfiguration).GetProperty("RemoteAccessWebRtcEnabled"), Is.Not.Null);
            Assert.That(typeof(ClientConfiguration).GetProperty("RemoteAccessWebRtcPolicyJson"), Is.Not.Null);
            Assert.That(typeof(ClientConfiguration).GetProperty("RemoteAccessWebRtcEnabled"), Is.Not.Null);
            Assert.That(typeof(SiteConfiguration).GetProperty("RemoteAccessWebRtcPolicyJson"), Is.Not.Null);
            Assert.That(typeof(SiteConfiguration).GetProperty("RemoteAccessWebRtcEnabled"), Is.Not.Null);
            Assert.That(typeof(Core.ValueObjects.ResolvedConfiguration).GetProperty("RemoteAccessWebRtc"), Is.Not.Null);
            Assert.That(typeof(Core.ValueObjects.ResolvedConfiguration).GetProperty("RemoteAccessWebRtcEnabled"), Is.Not.Null);
        });
    }

    [Test]
    public void Servidor_nasce_com_webrtc_desligado()
    {
        // A migration não pode mudar o transporte de ninguém no rollout.
        var server = new ServerConfiguration();

        Assert.Multiple(() =>
        {
            Assert.That(server.RemoteAccessWebRtcPolicyJson, Is.EqualTo("{}"));
            Assert.That(server.RemoteAccessWebRtcEnabled, Is.False);
            Assert.That(RemoteAccessWebRtcPolicy.Parse(server.RemoteAccessWebRtcPolicyJson).Enabled, Is.False);
        });
    }

    // ── Sanitização / idempotência ───────────────────────────────────────

    [Test]
    public void Sanitized_e_idempotente()
    {
        var once = RemoteAccessWebRtcPolicy.Parse(EnabledWithTurn).Sanitized();
        var twice = once.Sanitized();

        Assert.That(twice.ToJson(), Is.EqualTo(once.ToJson()));
    }

    [Test]
    public void Timeouts_fora_de_faixa_voltam_ao_default()
    {
        var policy = RemoteAccessWebRtcPolicy.Parse(
            """{"enabled":true,"ice":{"failedTimeoutSeconds":9999}}""");

        Assert.That(policy.Ice.FailedTimeoutSeconds, Is.EqualTo(15));
    }

    [Test]
    public void Round_trip_de_serializacao_preserva_a_politica()
    {
        var original = RemoteAccessWebRtcPolicy.Parse(EnabledWithTurn);
        var restored = RemoteAccessWebRtcPolicy.Parse(original.ToJson());

        Assert.Multiple(() =>
        {
            Assert.That(restored.Enabled, Is.EqualTo(original.Enabled));
            Assert.That(restored.EffectiveKinds, Is.EqualTo(original.EffectiveKinds));
            Assert.That(restored.Ice.TurnUrls, Is.EqualTo(original.Ice.TurnUrls));
            Assert.That(restored.Ice.RelayOnly, Is.EqualTo(original.Ice.RelayOnly));
        });
    }

    [Test]
    public void IsUnconfigured_distingue_default_de_corrompido()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RemoteAccessWebRtcPolicy.IsUnconfigured(null), Is.True);
            Assert.That(RemoteAccessWebRtcPolicy.IsUnconfigured("{}"), Is.True);
            Assert.That(RemoteAccessWebRtcPolicy.IsUnconfigured(EnabledWithTurn), Is.False);
            Assert.That(RemoteAccessWebRtcPolicy.IsUnconfigured("{corrompido"), Is.False,
                "corrompido não é 'não configurado' — não pode cair no bootstrap em silêncio");
            Assert.That(RemoteAccessWebRtcPolicy.IsUnconfigured("[1,2]"), Is.False,
                "documento que não é objeto é corrupção, não default");
        });
    }

    // ── Helpers (mesmo padrão de RemoteProxyPolicyInheritanceTests) ──────

    private static async Task<Guid> SeedAsync(
        DiscoveryDbContext db,
        string? server, string? client, string? site,
        bool serverEnabled, bool? clientEnabled, bool? siteEnabled)
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();

        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            RemoteAccessWebRtcPolicyJson = server ?? "{}",
            RemoteAccessWebRtcEnabled = serverEnabled,
        });

        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site Teste" });

        db.ClientConfigurations.Add(new ClientConfiguration
        {
            Id = IdGenerator.NewId(),
            ClientId = clientId,
            RemoteAccessWebRtcPolicyJson = client,
            RemoteAccessWebRtcEnabled = clientEnabled,
        });

        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = IdGenerator.NewId(),
            SiteId = siteId,
            ClientId = clientId,
            RemoteAccessWebRtcPolicyJson = site,
            RemoteAccessWebRtcEnabled = siteEnabled,
        });

        await db.SaveChangesAsync();
        return siteId;
    }

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"remote-webrtc-policy-{Guid.NewGuid():N}")
            .Options;
        return new ReducedDbContext(options);
    }

    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();

    private static ConfigurationResolver BuildResolver(DiscoveryDbContext db)
    {
        var redis = new NoopRedisService();
        return new ConfigurationResolver(
            new ServerConfigurationRepository(db, redis, EmptyConfiguration()),
            new ClientConfigurationRepository(db),
            new SiteConfigurationRepository(db),
            new SiteRepository(db),
            redis,
            new NoopSecretProtector());
    }

    private sealed class ReducedDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(ServerConfiguration), typeof(ClientConfiguration), typeof(SiteConfiguration),
                typeof(Client), typeof(Site), typeof(Agent),
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }
        }
    }

    private sealed class NoopRedisService : IRedisService
    {
        public bool IsConnected => false;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<long> IncrementAsync(string key) => Task.FromResult(0L);
        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(0L);
        public Task SetAsync(string key, string value, int expirySeconds = 3600) => Task.CompletedTask;
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(false);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(-1);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds) => Task.FromResult(true);
    }

    private sealed class NoopSecretProtector : ISecretProtector
    {
        public bool IsEnabled => false;
        public string Protect(string plainText) => plainText;
        public string Unprotect(string cipherText) => cipherText;
        public string UnprotectOrSelf(string? value) => value ?? string.Empty;
        public bool IsProtected(string? value) => false;
    }
}
