using System.Text.Json;
using System.Text.Json.Serialization;

namespace Discovery.Core.Configuration;

/// <summary>
/// Política de proxy/túnel do acesso remoto, persistida em
/// <c>RemoteProxyPolicyJson</c> (Server/Client/SiteConfiguration).
///
/// Por que um blob JSON e não uma tabela de entradas: o campo precisa ser
/// HERDÁVEL (servidor → cliente → site) e AUDITADO. Passando pelo sistema de
/// configuração existente, a herança vem de ConfigurationResolver e a auditoria
/// vem de ConfigurationService (com hash chain) — de graça. Uma tabela própria
/// exigiria resolver herança e escrever auditoria à mão.
///
/// Antes disto a allowlist só existia em <c>RemoteAccess:Proxy:DefaultAllowlist</c>
/// (appsettings.json) e era lida direto de IOptions: nenhuma tela conseguia
/// alterá-la e o default vazio bloqueava TODO destino
/// ("allowlist vazia: acesso bloqueado para <ip>:<porta>").
///
/// FAIL-CLOSED: qualquer falha de leitura cai em <see cref="Mode"/> =
/// <c>allowlist</c> com lista vazia (= bloqueio total). O modo "open" NUNCA é
/// assumido por default nem por JSON corrompido.
/// </summary>
public class RemoteProxyPolicy
{
    /// <summary>Modo "somente pré-aprovados". Default e fallback de qualquer erro.</summary>
    public const string ModeAllowlist = "allowlist";

    /// <summary>Modo "liberado geral" (mapeia para enabled=false no agent).</summary>
    public const string ModeOpen = "open";

    /// <summary>
    /// <see cref="ModeAllowlist"/> (default, fail-closed) ou <see cref="ModeOpen"/>.
    /// </summary>
    public string Mode { get; set; } = ModeAllowlist;

    /// <summary>Destinos pré-aprovados (CIDR ou IP avulso + portas opcionais).</summary>
    public List<RemoteProxyEntry> Entries { get; set; } = [];

    /// <summary>
    /// Permite destinos de loopback (127.0.0.0/8, ::1, localhost). No agent isto
    /// é um gate SEPARADO da allowlist: o loopback só passa se este toggle estiver
    /// ligado E o endereço casar com uma entrada. Default false.
    /// </summary>
    public bool AllowLoopback { get; set; }

    /// <summary>
    /// Portas permitidas GLOBAIS. Vazio = qualquer porta dos destinos aprovados.
    /// </summary>
    public int[] AllowedPorts { get; set; } = [];

    /// <summary>Teto de bytes por resposta. 0 ou negativo = usa o default do servidor.</summary>
    public long MaxResponseBytes { get; set; }

    /// <summary>True quando o modo é "open" (comparação case-insensitive).</summary>
    [JsonIgnore]
    public bool IsOpen => string.Equals(Mode?.Trim(), ModeOpen, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lista efetiva de CIDRs/IPs. No modo open o agent recebe enabled=false e a
    /// lista é irrelevante; devolvemos as entradas mesmo assim para a UI exibir.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveAllowlist
        => Entries.Where(e => !string.IsNullOrWhiteSpace(e.Cidr))
                  .Select(e => e.Cidr.Trim())
                  .ToArray();

    /// <summary>
    /// Portas EFETIVAMENTE aplicadas pelo agent.
    ///
    /// O agent tem UM conjunto de portas por sessão (Allowlist.ports), não um por
    /// prefixo: as portas por entrada são uma conveniência de cadastro, e a
    /// união delas com <see cref="AllowedPorts"/> é o que de fato vale.
    ///
    /// Consequência que a UI precisa deixar explícita: declarar portas em uma
    /// entrada RESTRINGE a sessão inteira àquelas portas — não é "8006 só neste
    /// CIDR, resto livre". Sem isto, cadastrar [8006] numa entrada e deixar outra
    /// sem portas liberaria todas as portas para ambas (o agent pula o gate
    /// quando o conjunto está vazio).
    ///
    /// Vazio = todas as portas (comportamento do agent).
    /// </summary>
    [JsonIgnore]
    public int[] EffectiveAllowedPorts
        => Entries.SelectMany(e => e.Ports ?? [])
                  .Concat(AllowedPorts ?? [])
                  .Where(p => p is >= 1 and <= 65535)
                  .Distinct()
                  .Order()
                  .ToArray();

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Política default: allowlist vazia = bloqueio total (igual ao appsettings de hoje).</summary>
    public static RemoteProxyPolicy BlockAll() => new();

    /// <summary>
    /// True quando o JSON é o DEFAULT da migration ("{}"), isto é: nenhum escopo
    /// definiu política.
    ///
    /// Existe porque a coluna do servidor é NOT NULL DEFAULT '{}' e portanto
    /// nunca volta null. Distinguir "não configurado" de "configurado como
    /// vazio" é o que permite o handler cair no bootstrap do appsettings sem
    /// bloquear instalações que nunca abriram a tela.
    ///
    /// A assimetria com ConfigurationResolver é INTENCIONAL: o resolver alimenta
    /// o GET de configuração efetiva e reflete só o banco (sem política =
    /// bloqueio total); o handler decide o que o agent recebe e preserva o
    /// appsettings como bootstrap. Não "unifique" os dois sem revisar isso.
    /// </summary>
    public static bool IsUnconfigured(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;

        var trimmed = json.Trim();
        if (trimmed is "{}" or "null") return true;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);

            // Um documento que NÃO é objeto (array, número, string) é dado
            // corrompido, não "não configurado": tratá-lo como default faria o
            // handler cair no appsettings e ignorar a corrupção em silêncio. Só
            // o objeto vazio é o default da migration.
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            return !doc.RootElement.EnumerateObject().Any();
        }
        catch (JsonException)
        {
            // JSON corrompido NÃO é "não configurado": deixamos o Parse tratá-lo
            // (fail-closed), sem cair no appsettings.
            return false;
        }
    }

    /// <summary>
    /// Leitura TOLERANTE (usada no caminho de runtime/resolução). JSON
    /// vazio/inválido cai em bloqueio total — nunca em "open".
    /// </summary>
    public static RemoteProxyPolicy Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            return BlockAll();

        try
        {
            var parsed = JsonSerializer.Deserialize<RemoteProxyPolicy>(json, Options);
            return parsed?.Sanitized() ?? BlockAll();
        }
        catch (JsonException)
        {
            return BlockAll();
        }
        catch (NotSupportedException)
        {
            return BlockAll();
        }
    }

    /// <summary>
    /// Validação ESTRITA usada no PATCH de configuração: JSON inválido não pode
    /// ser salvo. O parser tolerante existe só para ler dados antigos/corrompidos.
    /// </summary>
    public static bool TryParseStrict(string? json, out RemoteProxyPolicy policy, out string? error)
    {
        policy = BlockAll();
        error = null;

        if (string.IsNullOrWhiteSpace(json))
            return true;

        try
        {
            // Desserializa SEM sanitizar primeiro: Sanitized() DESCARTA entradas
            // sem cidr e portas fora da faixa, então validar depois dele nunca
            // acusaria nada — o dado ruim já teria sido removido em silêncio, e
            // o operador salvaria uma política diferente da que digitou.
            policy = JsonSerializer.Deserialize<RemoteProxyPolicy>(json, Options) ?? BlockAll();
        }
        catch (JsonException ex)
        {
            error = $"RemoteProxyPolicyJson inválido: {ex.Message}";
            return false;
        }
        catch (NotSupportedException ex)
        {
            error = $"RemoteProxyPolicyJson inválido: {ex.Message}";
            return false;
        }

        foreach (var entry in policy.Entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry?.Cidr))
            {
                error = "RemoteProxyPolicyJson inválido: entrada sem 'cidr'.";
                return false;
            }

            foreach (var port in entry.Ports ?? [])
            {
                if (port is < 1 or > 65535)
                {
                    error = $"RemoteProxyPolicyJson inválido: porta {port} fora da faixa 1-65535.";
                    return false;
                }
            }
        }

        foreach (var port in policy.AllowedPorts ?? [])
        {
            if (port is < 1 or > 65535)
            {
                error = $"RemoteProxyPolicyJson inválido: porta {port} fora da faixa 1-65535.";
                return false;
            }
        }

        // Só depois de validado é que normalizamos (ordena/deduplica portas).
        policy = policy.Sanitized();
        return true;
    }

    /// <summary>Serializa para persistência/transporte.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>
    /// Normaliza o objeto: modo desconhecido vira <c>allowlist</c> (fail-closed),
    /// portas inválidas são descartadas, entradas sem CIDR são removidas.
    /// </summary>
    public RemoteProxyPolicy Sanitized()
    {
        var mode = IsOpen ? ModeOpen : ModeAllowlist; // desconhecido ⇒ allowlist

        var entries = (Entries ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e?.Cidr))
            .Select(e => new RemoteProxyEntry
            {
                Cidr = e!.Cidr!.Trim(),
                Ports = (e.Ports ?? []).Where(p => p is >= 1 and <= 65535).Distinct().Order().ToArray(),
                Note = string.IsNullOrWhiteSpace(e.Note) ? null : e.Note!.Trim()
            })
            .ToArray();

        return new RemoteProxyPolicy
        {
            Mode = mode,
            Entries = [.. entries],
            AllowLoopback = AllowLoopback,
            AllowedPorts = (AllowedPorts ?? []).Where(p => p is >= 1 and <= 65535).Distinct().Order().ToArray(),
            MaxResponseBytes = MaxResponseBytes > 0 ? MaxResponseBytes : 0
        };
    }
}

/// <summary>Um destino pré-aprovado do proxy: CIDR (192.168.1.0/24) ou IP avulso.</summary>
public class RemoteProxyEntry
{
    /// <summary>CIDR ou IP avulso. Ex.: "192.168.10.0/24" ou "10.0.0.5".</summary>
    public string Cidr { get; set; } = string.Empty;

    /// <summary>
    /// Portas permitidas para ESTE destino. Vazio = todas as portas (respeitando
    /// <see cref="RemoteProxyPolicy.AllowedPorts"/> quando preenchido).
    /// </summary>
    public int[] Ports { get; set; } = [];

    /// <summary>Anotação livre para o operador (ex.: "Proxmox matriz").</summary>
    public string? Note { get; set; }
}
