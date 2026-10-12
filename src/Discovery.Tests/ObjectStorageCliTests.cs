using Discovery.Api;
using Discovery.Core.Entities;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Contratos do CLI de object storage usado pelo instalador do servidor
/// (Discovery.Api --set/--show/--test-object-storage).
///
/// O instalador depende disso para gravar a configuracao no banco sem psql e
/// sem deixar o segredo em texto plano (o IConfigurationService criptografa via
/// SecretProtector). Estes testes travam: o parse das flags, a preservacao de
/// campos/segredo em reexecucao e a validacao do endpoint.
/// </summary>
public class ObjectStorageCliTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private static ServerConfiguration Config(
        string bucket = "",
        string endpoint = "",
        string region = "",
        string accessKey = "",
        string secretKey = "",
        bool pathStyle = false,
        bool sslVerify = true,
        int ttlHours = 24) => new()
    {
        ObjectStorageBucketName = bucket,
        ObjectStorageEndpoint = endpoint,
        ObjectStorageRegion = region,
        ObjectStorageAccessKey = accessKey,
        ObjectStorageSecretKey = secretKey,
        ObjectStorageUsePathStyle = pathStyle,
        ObjectStorageSslVerify = sslVerify,
        ObjectStorageUrlTtlHours = ttlHours,
    };

    // ── Parse ───────────────────────────────────────────────────────────────

    [Test]
    public void TryParse_SemFlags_Ignora()
    {
        var parsed = ObjectStorageCliMode.TryParse(["--recover-admin"], out _, out _);
        Assert.That(parsed, Is.False, "flags de outro modo nao podem ativar o CLI de storage");
    }

    [Test]
    public void TryParse_FlagsDeAcao()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ObjectStorageCliMode.TryParse(["--set-object-storage"], out var set, out _), Is.True);
            Assert.That(set.Set, Is.True);

            Assert.That(ObjectStorageCliMode.TryParse(["--show-object-storage"], out var show, out _), Is.True);
            Assert.That(show.Show, Is.True);

            Assert.That(ObjectStorageCliMode.TryParse(["--test-object-storage"], out var test, out _), Is.True);
            Assert.That(test.Test, Is.True);

            Assert.That(ObjectStorageCliMode.TryParse(["--set-object-storage", "--test-object-storage", "--force"], out var combo, out _), Is.True);
            Assert.That(combo.Set, Is.True);
            Assert.That(combo.Test, Is.True);
            Assert.That(combo.Force, Is.True);
        });
    }

    [Test]
    public void TryParse_ForceSozinho_ExigeAcao()
    {
        var parsed = ObjectStorageCliMode.TryParse(["--force"], out _, out var error);
        Assert.That(parsed, Is.True);
        Assert.That(error, Is.Not.Null.And.Not.Empty, "--force sem acao precisa de mensagem de uso");
    }

    [Test]
    public void TryParse_Help()
    {
        Assert.That(ObjectStorageCliMode.TryParse(["--object-storage-help"], out var options, out _), Is.True);
        Assert.That(options.ShowHelp, Is.True);
        Assert.That(options.Any, Is.False, "help nao e uma acao de escrita/consulta");
    }

    // ── Campos vindos do ambiente ───────────────────────────────────────────

    [Test]
    public void BuildUpdates_SemVariaveis_NaoGravaNada()
    {
        var notes = new List<string>();
        var updates = ObjectStorageCliMode.BuildUpdates(Env(), Config(), force: false, notes);

        Assert.That(updates, Is.Empty);
        Assert.That(notes, Is.Empty);
    }

    [Test]
    public void BuildUpdates_LeTodosOsCamposInformados()
    {
        var env = Env(
            (ObjectStorageCliMode.BucketEnv, " discovery-files "),
            (ObjectStorageCliMode.EndpointEnv, "https://tngplacas.com.br"),
            (ObjectStorageCliMode.RegionEnv, "us-east-1"),
            (ObjectStorageCliMode.AccessKeyEnv, "discoveryrustfs"),
            (ObjectStorageCliMode.SecretKeyEnv, "segredo"),
            (ObjectStorageCliMode.UsePathStyleEnv, "true"),
            (ObjectStorageCliMode.SslVerifyEnv, "true"),
            (ObjectStorageCliMode.UrlTtlHoursEnv, "24"));

        var notes = new List<string>();
        var updates = ObjectStorageCliMode.BuildUpdates(env, Config(), force: false, notes);

        Assert.Multiple(() =>
        {
            Assert.That(updates[nameof(ServerConfiguration.ObjectStorageBucketName)], Is.EqualTo("discovery-files"), "valor deve ser trimado");
            Assert.That(updates[nameof(ServerConfiguration.ObjectStorageEndpoint)], Is.EqualTo("https://tngplacas.com.br"));
            Assert.That(updates[nameof(ServerConfiguration.ObjectStorageRegion)], Is.EqualTo("us-east-1"));
            Assert.That(updates[nameof(ServerConfiguration.ObjectStorageAccessKey)], Is.EqualTo("discoveryrustfs"));
            Assert.That(updates[nameof(ServerConfiguration.ObjectStorageSecretKey)], Is.EqualTo("segredo"));
            Assert.That(updates[nameof(ServerConfiguration.ObjectStorageUsePathStyle)], Is.EqualTo(true));
            Assert.That(updates[nameof(ServerConfiguration.ObjectStorageSslVerify)], Is.EqualTo(true));
            Assert.That(updates[nameof(ServerConfiguration.ObjectStorageUrlTtlHours)], Is.EqualTo(24));
            Assert.That(notes, Is.Empty);
        });
    }

    [Test]
    public void BuildUpdates_PreservaSegredoExistenteSemForce()
    {
        var env = Env((ObjectStorageCliMode.SecretKeyEnv, "novo-segredo"));
        var current = Config(secretKey: "enc:v1:v1:valorantigo");
        var notes = new List<string>();

        var updates = ObjectStorageCliMode.BuildUpdates(env, current, force: false, notes);

        Assert.That(updates.ContainsKey(nameof(ServerConfiguration.ObjectStorageSecretKey)), Is.False,
            "reexecutar o instalador nao pode rotacionar a credencial");
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes[0], Does.Contain("--force"));
    }

    [Test]
    public void BuildUpdates_SubstituiSegredoComForce()
    {
        var env = Env((ObjectStorageCliMode.SecretKeyEnv, "novo-segredo"));
        var updates = ObjectStorageCliMode.BuildUpdates(env, Config(secretKey: "antigo"), force: true, new List<string>());

        Assert.That(updates[nameof(ServerConfiguration.ObjectStorageSecretKey)], Is.EqualTo("novo-segredo"));
    }

    [Test]
    public void BuildUpdates_ValorBooleanoInvalidoGeraNotaEPreservaCampo()
    {
        var env = Env(
            (ObjectStorageCliMode.UsePathStyleEnv, "talvez"),
            (ObjectStorageCliMode.UrlTtlHoursEnv, "abc"));
        var notes = new List<string>();

        var updates = ObjectStorageCliMode.BuildUpdates(env, Config(), force: false, notes);

        Assert.That(updates, Is.Empty);
        Assert.That(notes, Has.Count.EqualTo(2));
    }

    // ── Validacao da configuracao efetiva ───────────────────────────────────

    [Test]
    public void EffectiveSettings_AceitaEndpointComHostSemPath()
    {
        var updates = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(ServerConfiguration.ObjectStorageBucketName)] = "discovery-files",
            [nameof(ServerConfiguration.ObjectStorageEndpoint)] = "https://tngplacas.com.br",
            [nameof(ServerConfiguration.ObjectStorageRegion)] = "us-east-1",
            [nameof(ServerConfiguration.ObjectStorageAccessKey)] = "chave",
            [nameof(ServerConfiguration.ObjectStorageSecretKey)] = "segredo",
        };

        var settings = ObjectStorageCliMode.EffectiveSettings(Config(), updates);

        Assert.That(settings.Validate(), Is.Empty);
    }

    [Test]
    public void EffectiveSettings_RejeitaEndpointComPath()
    {
        var updates = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(ServerConfiguration.ObjectStorageBucketName)] = "discovery-files",
            [nameof(ServerConfiguration.ObjectStorageEndpoint)] = "https://tngplacas.com.br/storage",
            [nameof(ServerConfiguration.ObjectStorageRegion)] = "us-east-1",
            [nameof(ServerConfiguration.ObjectStorageAccessKey)] = "chave",
            [nameof(ServerConfiguration.ObjectStorageSecretKey)] = "segredo",
        };

        var settings = ObjectStorageCliMode.EffectiveSettings(Config(), updates);

        Assert.That(settings.Validate(), Is.Not.Empty,
            "o endpoint guardado e apenas host[:porta]; path quebraria a assinatura das URLs pre-assinadas");
    }

    [Test]
    public void EffectiveSettings_MantemValoresNaoInformados()
    {
        var current = Config(
            bucket: "atual",
            endpoint: "https://atual.example.com",
            region: "us-east-1",
            accessKey: "chave-atual",
            secretKey: "enc:v1:v1:x",
            pathStyle: true,
            sslVerify: false,
            ttlHours: 12);

        var settings = ObjectStorageCliMode.EffectiveSettings(current, new Dictionary<string, object>());

        Assert.Multiple(() =>
        {
            Assert.That(settings.BucketName, Is.EqualTo("atual"));
            Assert.That(settings.Endpoint, Is.EqualTo("https://atual.example.com"));
            Assert.That(settings.AccessKey, Is.EqualTo("chave-atual"));
            Assert.That(settings.UsePathStyle, Is.True);
            Assert.That(settings.SslVerify, Is.False);
            Assert.That(settings.UrlTtlHours, Is.EqualTo(12));
        });
    }

    // ── Saida ───────────────────────────────────────────────────────────────

    [Test]
    public void DescribeState_NuncaExpoeOSegredo()
    {
        var config = Config(
            bucket: "discovery-files",
            endpoint: "https://tngplacas.com.br",
            region: "us-east-1",
            accessKey: "chave",
            secretKey: "segredo-super-secreto",
            pathStyle: true,
            sslVerify: true);

        var described = ObjectStorageCliMode.DescribeState(config);

        Assert.That(described, Does.Not.Contain("segredo-super-secreto"));
        Assert.That(described, Does.Contain("secretConfigured: True"));
        Assert.That(described, Does.Contain("https://tngplacas.com.br/discovery-files/"),
            "o prefixo ajuda a conferir o location do nginx");
    }

    [Test]
    public void DescribeState_EndpointSemScheme_MostraPrefixoHttps()
    {
        var described = ObjectStorageCliMode.DescribeState(Config(bucket: "b", endpoint: "host:9000"));
        Assert.That(described, Does.Contain("https://host:9000/b/"));
    }

    // ── Idempotencia (nao regravar a cada update) ───────────────────────────

    private static ServerConfiguration Configurado() => Config(
        bucket: "discovery-files",
        endpoint: "https://tngplacas.com.br",
        region: "us-east-1",
        accessKey: "discoveryrustfs",
        secretKey: "enc:v1:v1:segredo",
        pathStyle: true,
        sslVerify: true,
        ttlHours: 24);

    private static Dictionary<string, object> UpdatesIguais()
        => ObjectStorageCliMode.BuildUpdates(
            Env(
                (ObjectStorageCliMode.BucketEnv, "discovery-files"),
                (ObjectStorageCliMode.EndpointEnv, "https://tngplacas.com.br"),
                (ObjectStorageCliMode.RegionEnv, "us-east-1"),
                (ObjectStorageCliMode.AccessKeyEnv, "discoveryrustfs"),
                (ObjectStorageCliMode.UsePathStyleEnv, "true"),
                (ObjectStorageCliMode.SslVerifyEnv, "true"),
                (ObjectStorageCliMode.UrlTtlHoursEnv, "24")),
            Configurado(),
            force: false,
            new List<string>());

    [Test]
    public void SelectChanged_ConfiguracaoIgual_NaoRegravaNada()
    {
        var changed = ObjectStorageCliMode.SelectChanged(Configurado(), UpdatesIguais());

        Assert.That(changed, Is.Empty,
            "update do servidor nao pode regravar o storage quando nada mudou (Version/audit artificiais)");
    }

    [Test]
    public void SelectChanged_GravaSomenteOCampoQueMudou()
    {
        var env = Env(
            (ObjectStorageCliMode.BucketEnv, "discovery-files"),
            (ObjectStorageCliMode.EndpointEnv, "https://tngplacas.com.br"),
            (ObjectStorageCliMode.RegionEnv, "eu-west-1"),
            (ObjectStorageCliMode.AccessKeyEnv, "discoveryrustfs"),
            (ObjectStorageCliMode.UsePathStyleEnv, "true"),
            (ObjectStorageCliMode.SslVerifyEnv, "true"),
            (ObjectStorageCliMode.UrlTtlHoursEnv, "48"));
        var updates = ObjectStorageCliMode.BuildUpdates(env, Configurado(), force: false, new List<string>());

        var changed = ObjectStorageCliMode.SelectChanged(Configurado(), updates);

        Assert.Multiple(() =>
        {
            Assert.That(changed.Keys, Is.EquivalentTo(new[]
            {
                nameof(ServerConfiguration.ObjectStorageRegion),
                nameof(ServerConfiguration.ObjectStorageUrlTtlHours),
            }));
            Assert.That(changed[nameof(ServerConfiguration.ObjectStorageRegion)], Is.EqualTo("eu-west-1"));
            Assert.That(changed[nameof(ServerConfiguration.ObjectStorageUrlTtlHours)], Is.EqualTo(48));
        });
    }

    [Test]
    public void SelectChanged_DiferencaBooleana_GravaCampo()
    {
        var env = Env(
            (ObjectStorageCliMode.UsePathStyleEnv, "false"),
            (ObjectStorageCliMode.InternalEnv, "true"));
        var updates = ObjectStorageCliMode.BuildUpdates(env, Configurado(), force: false, new List<string>());

        var changed = ObjectStorageCliMode.SelectChanged(Configurado(), updates);

        Assert.Multiple(() =>
        {
            Assert.That(changed[nameof(ServerConfiguration.ObjectStorageUsePathStyle)], Is.EqualTo(false));
            Assert.That(changed[nameof(ServerConfiguration.ObjectStorageInternal)], Is.EqualTo(true));
        });
    }

    [Test]
    public void SelectChanged_EndpointComBarraFinalOuCase_ConsideraIgual()
    {
        var current = Config(
            bucket: "discovery-files",
            endpoint: "https://TngPlacas.com.br/",
            region: "us-east-1",
            accessKey: "discoveryrustfs",
            secretKey: "enc:v1:v1:segredo",
            pathStyle: true,
            sslVerify: true,
            ttlHours: 24);
        var updates = ObjectStorageCliMode.BuildUpdates(
            Env((ObjectStorageCliMode.EndpointEnv, "https://tngplacas.com.br")),
            current,
            force: false,
            new List<string>());

        var changed = ObjectStorageCliMode.SelectChanged(current, updates);

        Assert.That(changed, Is.Empty, "barra final/case nao podem disparar regravacao");
    }

    [Test]
    public void SelectChanged_SegredoComForce_SempreRegrava()
    {
        var current = Configurado();
        var updates = ObjectStorageCliMode.BuildUpdates(
            Env((ObjectStorageCliMode.SecretKeyEnv, "novo-segredo")),
            current,
            force: true,
            new List<string>());

        var changed = ObjectStorageCliMode.SelectChanged(current, updates);

        Assert.That(changed[nameof(ServerConfiguration.ObjectStorageSecretKey)], Is.EqualTo("novo-segredo"),
            "--force precisa substituir o segredo gravado");
    }

    [Test]
    public void SelectChanged_SegredoAusente_NaoMexeNoCampo()
    {
        var updates = ObjectStorageCliMode.BuildUpdates(Env(), Configurado(), force: true, new List<string>());

        var changed = ObjectStorageCliMode.SelectChanged(Configurado(), updates);

        Assert.That(changed.ContainsKey(nameof(ServerConfiguration.ObjectStorageSecretKey)), Is.False);
    }
}
