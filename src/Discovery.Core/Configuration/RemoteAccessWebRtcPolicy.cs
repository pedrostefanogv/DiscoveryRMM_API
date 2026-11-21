using System.Text.Json;
using System.Text.Json.Serialization;

namespace Discovery.Core.Configuration;

/// <summary>
/// Política de WebRTC do acesso remoto, persistida em
/// <c>RemoteAccessWebRtcPolicyJson</c> (Server/Client/SiteConfiguration).
///
/// Por que um blob JSON e não colunas soltas: o campo precisa ser HERDÁVEL
/// (servidor → cliente → site) e AUDITADO. Passando pelo sistema de configuração
/// existente, a herança vem de ConfigurationResolver e a auditoria vem de
/// ConfigurationService (com hash chain) — de graça. É o MESMO desenho de
/// <see cref="RemoteProxyPolicy"/>, e a assimetria entre os dois seria uma
/// armadilha para quem mantém.
///
/// FAIL-CLOSED: qualquer falha de leitura cai em <see cref="Enabled"/> = false,
/// isto é, o comportamento ANTERIOR (tudo pelo NATS). O WebRTC nunca é ligado
/// por JSON corrompido, por campo ausente ou por escopo que "parecia"
/// configurado. Isto é o oposto do proxy (que fecha em "bloqueia tudo") pela
/// mesma razão: o default tem de ser o comportamento já validado em produção.
///
/// ATIVAÇÃO: o default é <c>false</c> de propósito. O caminho NATS é o caminho
/// testado; o WebRTC é ADITIVO e precisa ser ligado explicitamente, no escopo
/// que o operador escolher.
/// </summary>
public class RemoteAccessWebRtcPolicy
{
    /// <summary>STUN público do Google. Default de convergência (MeshCentral e Breeze).</summary>
    public const string DefaultStunGoogle = "stun:stun.l.google.com:19302";

    /// <summary>STUN público da Cloudflare. Segundo default de convergência.</summary>
    public const string DefaultStunCloudflare = "stun:stun.cloudflare.com:3478";

    /// <summary>
    /// Kinds que PODEM usar WebRTC.
    ///
    /// Excluídos, com o motivo:
    ///   - TERMINAL: o gargalo é um único round-trip mais o coalescimento local
    ///     de 16 ms (session_terminal.go), e o volume é de poucos KB/s. O WebRTC
    ///     adicionaria handshake ICE e um caminho de falha sem ganho perceptível.
    ///   - REGISTRY: já é PAGINADO em 500 itens (winregistry/ops_windows.go)
    ///     justamente porque uma resposta única estouraria o limite de mensagem
    ///     do NATS. Ali o gargalo é o TAMANHO da página, não o transporte —
    ///     trocar o transporte não ajudaria e a rajada SCTP seria pior.
    ///   - PROCESSES/SERVICES: request/response por clique, volume pequeno e uso
    ///     esporádico. Ganho marginal; vale apenas de carona no "all".
    ///
    /// PROXY está aqui porque usa o MESMO SessionTunnel do RDP (manager.go:
    /// runProxySession e runRdpSession constroem o mesmo NewSessionTunnel, com o
    /// mesmo framing binário de 6 B e os mesmos subjects .tunnel.*). Excluí-lo
    /// seria incoerente: tecnicamente é o mesmo caso de multiplexação TCP — e é
    /// onde o P2P mais ajuda, porque o destino (Proxmox, iDRAC, switch) costuma
    /// estar em outra rede.
    /// </summary>
    public static readonly string[] AllowedKinds = ["screen", "rdp", "files", "proxy", "all"];

    /// <summary>
    /// Liga o WebRTC. Default <c>false</c>: preserva o comportamento atual e
    /// torna a mudança de transporte uma decisão explícita do operador.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// A quais tipos de sessão o WebRTC se aplica. Vazio (com <see cref="Enabled"/>
    /// true) é tratado como "nenhum" — nunca como "todos": um default implícito
    /// de "todos" ligaria WebRTC em terminal, que é justamente o caso sem ganho.
    /// </summary>
    public List<string> Kinds { get; set; } = ["screen", "rdp"];

    /// <summary>Configuração de ICE (STUN/TURN) e seus tempos.</summary>
    public WebRtcIceOptions Ice { get; set; } = new();

    /// <summary>
    /// Se o ICE falhar (NAT hostil, firewall, TURN inacessível), cai para o
    /// caminho NATS em vez de encerrar a sessão. Default <c>true</c>: o operador
    /// nunca deve ficar sem sessão por causa de uma otimização de transporte.
    /// </summary>
    public bool FallbackToNats { get; set; } = true;

    /// <summary>
    /// H.264 permanece desativado (patente AVC). O campo existe para tornar a
    /// decisão EXPLÍCITA e travá-la: ler <c>true</c> de um JSON adulterado não
    /// liga o encoder — ver <see cref="Sanitized"/>, que força false.
    /// </summary>
    public bool H264Enabled { get; set; }

    /// <summary>True quando o WebRTC está ligado E há ao menos um kind válido.</summary>
    [JsonIgnore]
    public bool IsEffective => Enabled && EffectiveKinds.Count > 0;

    /// <summary>Kinds efetivos, normalizados e restritos a <see cref="AllowedKinds"/>.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveKinds
        => (Kinds ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToLowerInvariant())
            .Where(k => AllowedKinds.Contains(k))
            .Distinct()
            .ToArray();

    /// <summary>
    /// Servidores ICE efetivos no formato que o runtime (pion/webrtc e os
    /// navegadores) espera. STUN sempre; TURN só quando há URL E credencial.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<WebRtcIceServer> EffectiveIceServers
        => Ice?.BuildIceServers() ?? [];

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Política default: WebRTC desligado = comportamento atual.</summary>
    public static RemoteAccessWebRtcPolicy Disabled() => new();

    /// <summary>
    /// True quando o JSON é o DEFAULT da migration ("{}"), isto é: nenhum escopo
    /// definiu política. A coluna do servidor é NOT NULL DEFAULT '{}' e portanto
    /// nunca volta null — distinguir "não configurado" de "configurado vazio" é
    /// o que permite cair no bootstrap do appsettings sem sobrescrever quem
    /// configurou de propósito.
    ///
    /// Um documento que NÃO é objeto (array, número, string) é dado CORROMPIDO,
    /// não "não configurado": tratá-lo como default esconderia a corrupção.
    /// </summary>
    public static bool IsUnconfigured(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;

        var trimmed = json.Trim();
        if (trimmed is "{}" or "null") return true;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            return !doc.RootElement.EnumerateObject().Any();
        }
        catch (JsonException)
        {
            // JSON corrompido NÃO é "não configurado": Parse o trata fail-closed.
            return false;
        }
    }

    /// <summary>
    /// Leitura TOLERANTE (runtime/resolução). JSON vazio ou inválido cai em
    /// WebRTC DESLIGADO — nunca em ligado.
    /// </summary>
    public static RemoteAccessWebRtcPolicy Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            return Disabled();

        try
        {
            var parsed = JsonSerializer.Deserialize<RemoteAccessWebRtcPolicy>(json, Options);
            return parsed?.Sanitized() ?? Disabled();
        }
        catch (JsonException)
        {
            return Disabled();
        }
        catch (NotSupportedException)
        {
            return Disabled();
        }
    }

    /// <summary>
    /// Validação ESTRITA usada no PATCH de configuração: JSON inválido não pode
    /// ser salvo. O parser tolerante existe só para ler dados antigos/corrompidos.
    /// </summary>
    public static bool TryParseStrict(string? json, out RemoteAccessWebRtcPolicy policy, out string? error)
    {
        policy = Disabled();
        error = null;

        if (string.IsNullOrWhiteSpace(json))
            return true;

        try
        {
            // Desserializa SEM sanitizar: Sanitized() descarta kind inválido e
            // força h264=false, então validar depois dele nunca acusaria nada —
            // o operador salvaria uma política diferente da que digitou.
            policy = JsonSerializer.Deserialize<RemoteAccessWebRtcPolicy>(json, Options) ?? Disabled();
        }
        catch (JsonException ex)
        {
            error = $"RemoteAccessWebRtcPolicyJson inválido: {ex.Message}";
            return false;
        }
        catch (NotSupportedException ex)
        {
            error = $"RemoteAccessWebRtcPolicyJson inválido: {ex.Message}";
            return false;
        }

        foreach (var kind in policy.Kinds ?? [])
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                error = "RemoteAccessWebRtcPolicyJson inválido: 'kinds' contém entrada vazia.";
                return false;
            }

            if (!AllowedKinds.Contains(kind.Trim().ToLowerInvariant()))
            {
                error = $"RemoteAccessWebRtcPolicyJson inválido: kind '{kind}' não é suportado "
                      + $"(aceitos: {string.Join(", ", AllowedKinds)}). "
                      + "Terminal não usa WebRTC: o ganho não justifica o custo.";
                return false;
            }
        }

        var iceError = policy.Ice?.Validate();
        if (iceError is not null)
        {
            error = $"RemoteAccessWebRtcPolicyJson inválido: {iceError}";
            return false;
        }

        // H.264 é decisão travada: recusar explicitamente é melhor que aceitar
        // e silenciosamente desligar depois (o operador acharia que ligou).
        if (policy.H264Enabled)
        {
            error = "RemoteAccessWebRtcPolicyJson inválido: H.264 não é suportado "
                  + "(patente AVC; encoder desativado no agente). Use VP8/VP9.";
            return false;
        }

        // Só depois de validado normalizamos (ordena/deduplica kinds).
        policy = policy.Sanitized();
        return true;
    }

    /// <summary>Serializa para persistência/transporte.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>
    /// Normaliza: kinds fora da allowlist são descartados, H.264 é forçado a
    /// false, timeouts fora de faixa voltam ao default e listas são deduplicadas.
    /// Idempotente.
    /// </summary>
    public RemoteAccessWebRtcPolicy Sanitized() => new()
    {
        Enabled = Enabled,
        Kinds = [.. EffectiveKinds],
        Ice = (Ice ?? new WebRtcIceOptions()).Sanitized(),
        FallbackToNats = FallbackToNats,
        // H.264 é SEMPRE false, venha o que vier no JSON.
        H264Enabled = false
    };
}

/// <summary>
/// Configuração de ICE (STUN/TURN) e tempos do peer connection.
///
/// Os defaults de STUN são os servidores públicos gratuitos que o pedido
/// especificou (Google e Cloudflare) — e são também o que MeshCentral e Breeze
/// escolheram de forma independente (ver PESQUISA_WEBRTC_REFERENCIAS).
/// </summary>
public class WebRtcIceOptions
{
    /// <summary>
    /// Servidores STUN. Pré-preenchidos com os públicos gratuitos. Vazio é
    /// permitido (rede fechada/LAN): sem STUN só há candidatos de host, o que
    /// funciona em LAN e degrada fora — não é erro.
    /// </summary>
    public List<string> StunUrls { get; set; } =
        [RemoteAccessWebRtcPolicy.DefaultStunGoogle, RemoteAccessWebRtcPolicy.DefaultStunCloudflare];

    /// <summary>Servidores TURN. Vazio por padrão: TURN exige infraestrutura do operador.</summary>
    public List<string> TurnUrls { get; set; } = [];

    /// <summary>Usuário do TURN (credencial de longo prazo ou emitida por sessão).</summary>
    public string TurnUsername { get; set; } = string.Empty;

    /// <summary>
    /// Credencial do TURN. SEGREDO: cifrado em repouso e NUNCA devolvido em
    /// claro no GET de configuração — ver ConfigurationService/redação.
    /// </summary>
    public string TurnCredential { get; set; } = string.Empty;

    /// <summary>
    /// TTL das credenciais TURN emitidas por sessão (formato coturn
    /// <c>{expiry}:{sessionId}</c>). 0 usa o valor do servidor.
    /// </summary>
    public int TurnCredentialTtlMinutes { get; set; } = 60;

    /// <summary>
    /// Força o tráfego pelo TURN (policy <c>relay</c>), ignorando candidatos
    /// diretos. FAIL-CLOSED: com <c>true</c> e nenhum TURN configurado, a sessão
    /// é RECUSADA na API — não cai silenciosamente para STUN/direto.
    /// </summary>
    public bool RelayOnly { get; set; }

    /// <summary>
    /// Timeouts de ICE em segundos. Os defaults do pion (25 s de <c>failed</c>)
    /// são calibrados para videochamada; suporte remoto precisa detectar a falha
    /// e cair para o NATS rápido.
    /// </summary>
    public int DisconnectedTimeoutSeconds { get; set; } = 5;

    /// <inheritdoc cref="DisconnectedTimeoutSeconds"/>
    public int FailedTimeoutSeconds { get; set; } = 15;

    /// <inheritdoc cref="DisconnectedTimeoutSeconds"/>
    public int KeepAliveIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// Servidores ICE efetivos no formato do runtime. STUN sempre; TURN apenas
    /// quando há URL — a credencial é anexada somente se preenchida.
    /// </summary>
    public IReadOnlyList<WebRtcIceServer> BuildIceServers()
    {
        var servers = new List<WebRtcIceServer>();

        var stun = Clean(StunUrls, "stun:");
        if (stun.Count > 0)
            servers.Add(new WebRtcIceServer { Urls = stun });

        var turn = Clean(TurnUrls, "turn:", "turns:");
        if (turn.Count > 0)
        {
            servers.Add(new WebRtcIceServer
            {
                Urls = turn,
                Username = string.IsNullOrWhiteSpace(TurnUsername) ? null : TurnUsername.Trim(),
                Credential = string.IsNullOrWhiteSpace(TurnCredential) ? null : TurnCredential
            });
        }

        return servers;
    }

    /// <summary>True quando há ao menos um TURN configurado com URL válida.</summary>
    [JsonIgnore]
    public bool HasTurn => Clean(TurnUrls, "turn:", "turns:").Count > 0;

    /// <summary>
    /// Validação estrita. Devolve a mensagem de erro ou <c>null</c>.
    ///
    /// O caso mais importante é <see cref="RelayOnly"/> sem TURN: aceitar isto
    /// produziria uma sessão que NUNCA conecta (o navegador não tem candidato
    /// relay e o ICE falha em 15 s), com o operador vendo "conectando..." sem
    /// causa. Recusar na API transforma isso em erro explícito e imediato.
    /// </summary>
    public string? Validate()
    {
        foreach (var url in StunUrls ?? [])
        {
            if (!string.IsNullOrWhiteSpace(url) && !url.Trim().StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
                return $"endereço STUN '{url}' deve começar com 'stun:'.";
        }

        foreach (var url in TurnUrls ?? [])
        {
            if (string.IsNullOrWhiteSpace(url)) continue;

            var trimmed = url.Trim();
            if (!trimmed.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))
                return $"endereço TURN '{url}' deve começar com 'turn:' ou 'turns:'.";
        }

        if (RelayOnly && !HasTurn)
            return "relayOnly=true exige ao menos um servidor TURN: sem TURN a sessão nunca "
                 + "estabelece (não há candidato relay). Configure turnUrls ou desligue relayOnly.";

        foreach (var (name, value) in new[]
        {
            (nameof(DisconnectedTimeoutSeconds), DisconnectedTimeoutSeconds),
            (nameof(FailedTimeoutSeconds), FailedTimeoutSeconds),
            (nameof(KeepAliveIntervalSeconds), KeepAliveIntervalSeconds)
        })
        {
            if (value is < 1 or > 120)
                return $"{name} deve estar entre 1 e 120 segundos (recebido {value}).";
        }

        if (TurnCredentialTtlMinutes is < 1 or > 1440)
            return $"turnCredentialTtlMinutes deve estar entre 1 e 1440 (recebido {TurnCredentialTtlMinutes}).";

        return null;
    }

    /// <summary>Normaliza: trim, dedupe, preserva a ordem e limita os timeouts.</summary>
    public WebRtcIceOptions Sanitized() => new()
    {
        StunUrls = [.. (StunUrls ?? []).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).Distinct()],
        TurnUrls = [.. (TurnUrls ?? []).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).Distinct()],
        TurnUsername = TurnUsername?.Trim() ?? string.Empty,
        TurnCredential = TurnCredential ?? string.Empty,
        TurnCredentialTtlMinutes = TurnCredentialTtlMinutes is >= 1 and <= 1440 ? TurnCredentialTtlMinutes : 60,
        RelayOnly = RelayOnly,
        DisconnectedTimeoutSeconds = DisconnectedTimeoutSeconds is >= 1 and <= 120 ? DisconnectedTimeoutSeconds : 5,
        FailedTimeoutSeconds = FailedTimeoutSeconds is >= 1 and <= 120 ? FailedTimeoutSeconds : 15,
        KeepAliveIntervalSeconds = KeepAliveIntervalSeconds is >= 1 and <= 120 ? KeepAliveIntervalSeconds : 2
    };

    private static List<string> Clean(IEnumerable<string>? urls, params string[] prefixes)
        => (urls ?? [])
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Where(u => prefixes.Any(p => u.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();
}

/// <summary>
/// Um servidor ICE no formato de wire (camelCase), consumido tanto pelo
/// navegador (`new RTCPeerConnection({ iceServers })`) quanto pelo agente
/// (pion). É o mesmo contrato que o Breeze usa para aceitar <c>urls</c> como
/// string OU array — aqui a API sempre EMITE array, que é o formato aceito
/// pelos dois lados.
/// </summary>
public class WebRtcIceServer
{
    /// <summary>URLs do servidor (stun:, turn:, turns:).</summary>
    public IReadOnlyList<string> Urls { get; set; } = [];

    /// <summary>Usuário TURN (ausente em STUN).</summary>
    public string? Username { get; set; }

    /// <summary>Credencial TURN (ausente em STUN).</summary>
    public string? Credential { get; set; }
}
