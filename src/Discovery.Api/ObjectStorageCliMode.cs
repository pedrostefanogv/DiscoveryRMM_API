using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;

namespace Discovery.Api;

/// <summary>
/// CLI de configuracao do object storage, usada pelo instalador do servidor.
///
/// Motivo: o instalador precisa gravar a configuracao de storage no banco
/// (tabela server_configurations) SEM depender de psql e SEM deixar o segredo em
/// texto plano quando SecretEncryption esta ligada. Passando pelo
/// IConfigurationService o segredo e criptografado pelo SecretProtector, os
/// campos sao validados e o audit (updated_by) e registrado — exatamente o mesmo
/// caminho que o console usa.
///
/// Uso:
///   Discovery.Api --set-object-storage [--test-object-storage] [--force]
///   Discovery.Api --show-object-storage
///   Discovery.Api --test-object-storage
///
/// Os valores vem de VARIAVEIS DE AMBIENTE (nunca de argv, para o segredo nao
/// aparecer em /proc/&lt;pid&gt;/cmdline):
///   DISCOVERY_OBJECT_STORAGE_BUCKET, _ENDPOINT, _REGION, _ACCESS_KEY,
///   _SECRET_KEY, _USE_PATH_STYLE, _SSL_VERIFY, _URL_TTL_HOURS
/// </summary>
internal static class ObjectStorageCliMode
{
    public const string BucketEnv = "DISCOVERY_OBJECT_STORAGE_BUCKET";
    public const string EndpointEnv = "DISCOVERY_OBJECT_STORAGE_ENDPOINT";
    public const string RegionEnv = "DISCOVERY_OBJECT_STORAGE_REGION";
    public const string AccessKeyEnv = "DISCOVERY_OBJECT_STORAGE_ACCESS_KEY";
    public const string SecretKeyEnv = "DISCOVERY_OBJECT_STORAGE_SECRET_KEY";
    public const string UsePathStyleEnv = "DISCOVERY_OBJECT_STORAGE_USE_PATH_STYLE";
    public const string SslVerifyEnv = "DISCOVERY_OBJECT_STORAGE_SSL_VERIFY";
    public const string UrlTtlHoursEnv = "DISCOVERY_OBJECT_STORAGE_URL_TTL_HOURS";
    /// <summary>Liga o storage interno (RustFS do servidor; chaves do env do servidor).</summary>
    public const string InternalEnv = "DISCOVERY_OBJECT_STORAGE_INTERNAL";

    private const string SetFlag = "--set-object-storage";
    private const string ShowFlag = "--show-object-storage";
    private const string TestFlag = "--test-object-storage";
    private const string ForceFlag = "--force";
    private const string HelpFlag = "--object-storage-help";

    /// <summary>Actor gravado em UpdatedBy quando o instalador faz a configuracao.</summary>
    internal const string Actor = "installer:object-storage-cli";

    internal sealed record Options(bool Set, bool Show, bool Test, bool Force, bool ShowHelp)
    {
        public static Options Default => new(Set: false, Show: false, Test: false, Force: false, ShowHelp: false);

        /// <summary>Ha alguma acao real (help sozinho nao conta).</summary>
        public bool Any => Set || Show || Test;
    }

    internal static bool TryParse(string[] args, out Options options, out string? error)
    {
        options = Options.Default;
        error = null;

        if (!args.Any(IsKnownFlag))
            return false;

        var set = false;
        var show = false;
        var test = false;
        var force = false;
        var help = false;

        foreach (var arg in args)
        {
            if (Equals(arg, SetFlag)) set = true;
            else if (Equals(arg, ShowFlag)) show = true;
            else if (Equals(arg, TestFlag)) test = true;
            else if (Equals(arg, ForceFlag)) force = true;
            else if (Equals(arg, HelpFlag)) help = true;
        }

        options = new Options(set, show, test, force, help);

        if (!set && !show && !test && !help)
            error = "Informe uma acao de object storage: --set-object-storage, --show-object-storage, --test-object-storage ou --object-storage-help.";

        return true;
    }

    internal static void PrintHelp()
    {
        Console.WriteLine("Discovery API - configuracao do object storage (usado pelo instalador)");
        Console.WriteLine("Usage:");
        Console.WriteLine("  Discovery.Api --set-object-storage [--test-object-storage] [--force]");
        Console.WriteLine("  Discovery.Api --show-object-storage");
        Console.WriteLine("  Discovery.Api --test-object-storage");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --set-object-storage     Grava no banco o que estiver nas variaveis de ambiente");
        Console.WriteLine("  --show-object-storage    Mostra o estado atual (segredo sempre mascarado)");
        Console.WriteLine("  --test-object-storage    Testa a conexao/bucket com a configuracao atual");
        Console.WriteLine("  --force                  Regrava mesmo sem mudanca e substitui o segredo (default: preserva)");
        Console.WriteLine("  --object-storage-help    Mostra esta ajuda");
        Console.WriteLine();
        Console.WriteLine("Variaveis de ambiente lidas por --set-object-storage:");
        foreach (var name in new[]
                 {
                     BucketEnv, EndpointEnv, RegionEnv, AccessKeyEnv, SecretKeyEnv,
                     UsePathStyleEnv, SslVerifyEnv, UrlTtlHoursEnv, InternalEnv,
                 })
        {
            Console.WriteLine("  " + name);
        }
        Console.WriteLine();
        Console.WriteLine("Campos ausentes sao preservados; o endpoint deve ser host/host:porta");
        Console.WriteLine("(sem path) e o segredo nunca e impresso.");
        Console.WriteLine("Sem --force, campos iguais aos ja gravados nao sao reescritos (idempotente).");
    }

    internal static async Task<int> ExecuteAsync(IServiceProvider services, Options options)
    {
        if (!options.Any)
            return 0;

        await using var scope = services.CreateAsyncScope();
        var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
        var storageFactory = scope.ServiceProvider.GetRequiredService<IObjectStorageProviderFactory>();

        if (options.Set)
        {
            var current = await configService.GetServerConfigAsync();
            var notes = new List<string>();
            var updates = BuildUpdates(Environment.GetEnvironmentVariable, current, options.Force, notes);
            foreach (var note in notes)
                Console.WriteLine("[object-storage] " + note);

            if (updates.Count == 0)
            {
                Console.WriteLine("[object-storage] Nenhuma variavel DISCOVERY_OBJECT_STORAGE_* informada; nada a gravar.");
            }
            else
            {
                // Modo interno: chaves/loopback vem do env do servidor, entao a
                // validacao de endpoint/credenciais no banco nao se aplica.
                var internalMode = updates.TryGetValue(nameof(ServerConfiguration.ObjectStorageInternal), out var internalValue)
                    && internalValue is bool flag && flag;
                var validationErrors = internalMode ? [] : EffectiveSettings(current, updates).Validate();
                if (validationErrors.Count > 0)
                {
                    Console.Error.WriteLine("[object-storage] Configuracao invalida:");
                    foreach (var e in validationErrors)
                        Console.Error.WriteLine("  - " + e);
                    return 1;
                }

                // Idempotencia: sem --force so grava o que realmente mudou. O update
                // do servidor chama este CLI em TODA execucao; regravar sempre (mesmo
                // com valores iguais) inflava Version/UpdatedBy e o audit de
                // server_configurations a cada update sem nenhuma mudanca real.
                var toSave = options.Force ? updates : SelectChanged(current, updates);
                if (toSave.Count == 0)
                {
                    Console.WriteLine("[object-storage] Configuracao ja gravada e igual ao ambiente; nada a regravar (use --force para regravar).");
                    Console.WriteLine(DescribeState(current));
                }
                else
                {
                    var saved = await configService.PatchServerAsync(toSave, Actor);
                    Console.WriteLine("[object-storage] Configuracao gravada.");
                    if (toSave.Count < updates.Count)
                        Console.WriteLine($"[object-storage] {updates.Count - toSave.Count} campo(s) inalterado(s) preservado(s).");
                    Console.WriteLine(DescribeState(saved));
                }
            }
        }

        if (options.Show || (options.Set && !options.Test))
        {
            var current = await configService.GetServerConfigAsync();
            Console.WriteLine("[object-storage] Estado atual:");
            Console.WriteLine(DescribeState(current));
        }

        if (options.Test)
        {
            // TestConnectionAsync le a configuracao do repositorio (sem o cache de
            // servico), entao o teste enxerga o que acabou de ser gravado.
            var result = await storageFactory.TestConnectionAsync();
            Console.WriteLine(
                $"[object-storage] Teste: success={result.Success} configValid={result.ConfigurationValid} " +
                $"bucketReachable={result.BucketReachable} latencyMs={result.LatencyMs}");
            foreach (var e in result.Errors)
                Console.Error.WriteLine("  - " + e);

            if (!result.Success || !result.BucketReachable)
                return 1;
        }

        return 0;
    }

    /// <summary>
    /// Monta o dicionario de campos a gravar a partir das variaveis de ambiente.
    ///
    /// Regras: campo ausente/vazio e preservado; o segredo so e substituido se
    /// ainda nao houver um configurado ou se force=true (evita rotacionar a
    /// credencial em uma reexecucao do instalador).
    /// </summary>
    internal static Dictionary<string, object> BuildUpdates(
        Func<string, string?> read,
        ServerConfiguration current,
        bool force,
        List<string> notes)
    {
        var updates = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        void SetString(string envName, string property)
        {
            var value = read(envName)?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                updates[property] = value!;
        }

        SetString(BucketEnv, nameof(ServerConfiguration.ObjectStorageBucketName));
        SetString(EndpointEnv, nameof(ServerConfiguration.ObjectStorageEndpoint));
        SetString(RegionEnv, nameof(ServerConfiguration.ObjectStorageRegion));
        SetString(AccessKeyEnv, nameof(ServerConfiguration.ObjectStorageAccessKey));

        var secret = read(SecretKeyEnv)?.Trim();
        if (!string.IsNullOrWhiteSpace(secret))
        {
            if (!force && !string.IsNullOrWhiteSpace(current.ObjectStorageSecretKey))
            {
                notes.Add("Segredo do storage ja configurado: mantido (use --force para substituir).");
            }
            else
            {
                updates[nameof(ServerConfiguration.ObjectStorageSecretKey)] = secret!;
            }
        }

        var pathStyleRaw = read(UsePathStyleEnv)?.Trim();
        if (TryParseBool(pathStyleRaw, out var pathStyle))
            updates[nameof(ServerConfiguration.ObjectStorageUsePathStyle)] = pathStyle;
        else if (!string.IsNullOrWhiteSpace(pathStyleRaw))
            notes.Add($"Valor invalido em {UsePathStyleEnv} ({pathStyleRaw}): campo preservado.");

        var sslVerifyRaw = read(SslVerifyEnv)?.Trim();
        if (TryParseBool(sslVerifyRaw, out var sslVerify))
            updates[nameof(ServerConfiguration.ObjectStorageSslVerify)] = sslVerify;
        else if (!string.IsNullOrWhiteSpace(sslVerifyRaw))
            notes.Add($"Valor invalido em {SslVerifyEnv} ({sslVerifyRaw}): campo preservado.");

        var internalRaw = read(InternalEnv)?.Trim();
        if (TryParseBool(internalRaw, out var internalStorage))
            updates[nameof(ServerConfiguration.ObjectStorageInternal)] = internalStorage;
        else if (!string.IsNullOrWhiteSpace(internalRaw))
            notes.Add($"Valor invalido em {InternalEnv} ({internalRaw}): campo preservado.");

        var ttlRaw = read(UrlTtlHoursEnv)?.Trim();
        if (!string.IsNullOrWhiteSpace(ttlRaw))
        {
            if (int.TryParse(ttlRaw, out var ttl))
                updates[nameof(ServerConfiguration.ObjectStorageUrlTtlHours)] = ttl;
            else
                notes.Add($"Valor invalido em {UrlTtlHoursEnv} ({ttlRaw}): campo preservado.");
        }

        return updates;
    }

    /// <summary>
    /// Filtra o dicionario montado pelo ambiente mantendo APENAS os campos que
    /// diferem do que ja esta gravado. Usado por --set-object-storage sem --force
    /// para tornar o update idempotente: se nada mudou, PatchServerAsync nem e
    /// chamado (sem bump de Version, sem UpdatedBy artificial, sem audit).
    ///
    /// O segredo e sempre considerado alterado quando presente: ele pode estar
    /// protegido (SecretProtector) e nao ha comparacao confiavel em texto.
    /// </summary>
    internal static Dictionary<string, object> SelectChanged(
        ServerConfiguration current,
        IReadOnlyDictionary<string, object> updates)
    {
        var changed = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in updates)
        {
            if (IsDifferent(current, key, value))
                changed[key] = value;
        }

        return changed;
    }

    private static bool IsDifferent(ServerConfiguration current, string property, object? value)
    {
        switch (property)
        {
            case nameof(ServerConfiguration.ObjectStorageBucketName):
                return !SameText(current.ObjectStorageBucketName, value);
            case nameof(ServerConfiguration.ObjectStorageEndpoint):
                return !SameEndpoint(current.ObjectStorageEndpoint, value);
            case nameof(ServerConfiguration.ObjectStorageRegion):
                return !SameText(current.ObjectStorageRegion, value);
            case nameof(ServerConfiguration.ObjectStorageAccessKey):
                return !SameText(current.ObjectStorageAccessKey, value);
            case nameof(ServerConfiguration.ObjectStorageSecretKey):
                return true;
            case nameof(ServerConfiguration.ObjectStorageUsePathStyle):
                return value is bool pathStyle && pathStyle != current.ObjectStorageUsePathStyle;
            case nameof(ServerConfiguration.ObjectStorageSslVerify):
                return value is bool sslVerify && sslVerify != current.ObjectStorageSslVerify;
            case nameof(ServerConfiguration.ObjectStorageInternal):
                return value is bool internalStorage && internalStorage != current.ObjectStorageInternal;
            case nameof(ServerConfiguration.ObjectStorageUrlTtlHours):
                return value is int ttl && ttl != current.ObjectStorageUrlTtlHours;
            default:
                return true;
        }
    }

    private static bool SameText(string? current, object? value)
        => string.Equals(
            (current ?? string.Empty).Trim(),
            Convert.ToString(value)?.Trim() ?? string.Empty,
            StringComparison.Ordinal);

    /// <summary>
    /// Endpoint: comparado sem barra final e sem case. O banco guarda host[:porta]
    /// (as vezes com scheme) e o env pode trazer "https://host" vs "host".
    /// </summary>
    private static bool SameEndpoint(string? current, object? value)
        => string.Equals(
            (current ?? string.Empty).Trim().TrimEnd('/'),
            (Convert.ToString(value) ?? string.Empty).Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Configuracao efetiva (atual + campos informados) usada para validar ANTES
    /// de gravar. Para o segredo, o valor armazenado (possivelmente protegido)
    /// serve: Validate() so verifica se esta preenchido.
    /// </summary>
    internal static ObjectStorageSettings EffectiveSettings(
        ServerConfiguration current,
        IReadOnlyDictionary<string, object> updates)
    {
        string GetString(string property, string fallback)
            => updates.TryGetValue(property, out var value) && value is not null
                ? Convert.ToString(value) ?? fallback
                : fallback;

        bool GetBool(string property, bool fallback)
            => updates.TryGetValue(property, out var value) && value is bool flag ? flag : fallback;

        int GetInt(string property, int fallback)
            => updates.TryGetValue(property, out var value) && value is int number ? number : fallback;

        return new ObjectStorageSettings
        {
            BucketName = GetString(nameof(ServerConfiguration.ObjectStorageBucketName), current.ObjectStorageBucketName),
            Endpoint = GetString(nameof(ServerConfiguration.ObjectStorageEndpoint), current.ObjectStorageEndpoint),
            Region = GetString(nameof(ServerConfiguration.ObjectStorageRegion), current.ObjectStorageRegion),
            AccessKey = GetString(nameof(ServerConfiguration.ObjectStorageAccessKey), current.ObjectStorageAccessKey),
            SecretKey = GetString(nameof(ServerConfiguration.ObjectStorageSecretKey), current.ObjectStorageSecretKey),
            UrlTtlHours = GetInt(nameof(ServerConfiguration.ObjectStorageUrlTtlHours), current.ObjectStorageUrlTtlHours),
            UsePathStyle = GetBool(nameof(ServerConfiguration.ObjectStorageUsePathStyle), current.ObjectStorageUsePathStyle),
            SslVerify = GetBool(nameof(ServerConfiguration.ObjectStorageSslVerify), current.ObjectStorageSslVerify),
        };
    }

    /// <summary>Descricao legivel do estado, sempre sem expor o segredo.</summary>
    internal static string DescribeState(ServerConfiguration config)
    {
        var secretConfigured = !string.IsNullOrWhiteSpace(config.ObjectStorageSecretKey);
        var bucket = string.IsNullOrWhiteSpace(config.ObjectStorageBucketName) ? "(vazio)" : config.ObjectStorageBucketName;
        var endpoint = string.IsNullOrWhiteSpace(config.ObjectStorageEndpoint) ? "(vazio)" : config.ObjectStorageEndpoint;
        var region = string.IsNullOrWhiteSpace(config.ObjectStorageRegion) ? "(vazio)" : config.ObjectStorageRegion;

        // Prefixo das URLs pre-assinadas (path-style): ajuda a conferir se o
        // location do nginx cobre o bucket.
        var urlPrefix = string.IsNullOrWhiteSpace(config.ObjectStorageBucketName)
            || string.IsNullOrWhiteSpace(config.ObjectStorageEndpoint)
            ? "(indefinido)"
            : $"{NormalizeScheme(config.ObjectStorageEndpoint)}/{config.ObjectStorageBucketName}/";

        return string.Join(Environment.NewLine, new[]
        {
            $"  bucket: {bucket}",
            $"  endpoint: {endpoint}",
            $"  region: {region}",
            $"  usePathStyle: {config.ObjectStorageUsePathStyle}",
            $"  sslVerify: {config.ObjectStorageSslVerify}",
            $"  urlTtlHours: {config.ObjectStorageUrlTtlHours}",
            $"  internal: {config.ObjectStorageInternal}",
            $"  secretConfigured: {secretConfigured}",
            $"  urlPrefix: {urlPrefix}",
        });
    }

    private static string NormalizeScheme(string endpoint)
    {
        var trimmed = endpoint.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return trimmed.TrimEnd('/');

        return "https://" + trimmed.TrimEnd('/');
    }

    private static bool TryParseBool(string? raw, out bool value)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "y":
            case "on":
                value = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "n":
            case "off":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool IsKnownFlag(string arg)
        => Equals(arg, SetFlag)
           || Equals(arg, ShowFlag)
           || Equals(arg, TestFlag)
           || Equals(arg, ForceFlag)
           || Equals(arg, HelpFlag);

    private static bool Equals(string arg, string expected)
        => string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase);
}
