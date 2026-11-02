using Discovery.Api;
using Discovery.Core.Entities;

namespace Discovery.Tests;

/// <summary>
/// O update do servidor reexecuta o CLI do storage com as variaveis
/// DISCOVERY_OBJECT_STORAGE_* do instalador. Depois de o console migrar para um
/// provedor externo (ex.: Cloudflare R2), isso revertia endpoint/bucket/chaves
/// para o RustFS interno — aconteceu em homolog em 01/11/2026. Este guard impede
/// a regressão quando o banco já tem uma configuração externa completa.
/// </summary>
[TestFixture]
public class ObjectStorageCliGuardTests
{
    private static ServerConfiguration ExternalConsoleConfig() => new()
    {
        ObjectStorageInternal = false,
        ObjectStorageEndpoint = "https://<account>.r2.cloudflarestorage.com",
        ObjectStorageBucketName = "discovery-rmm",
        ObjectStorageAccessKey = "chave-r2",
        ObjectStorageSecretKey = "segredo-cifrado",
    };

    [Test]
    public void Skips_WhenConsoleHasExternalConfig_AndInstallerChangesTopology()
    {
        var pending = new Dictionary<string, object>
        {
            [nameof(ServerConfiguration.ObjectStorageEndpoint)] = "https://discovery.exemplo.com.br",
            [nameof(ServerConfiguration.ObjectStorageBucketName)] = "discovery-files",
            [nameof(ServerConfiguration.ObjectStorageInternal)] = true,
        };

        var skip = ObjectStorageCliMode.ShouldSkipConsoleManaged(ExternalConsoleConfig(), pending, out var reason);

        Assert.That(skip, Is.True);
        Assert.That(reason, Does.Contain("console"));
    }

    [Test]
    public void DoesNotSkip_WhenOnlyNonTopologyFieldsChange()
    {
        var pending = new Dictionary<string, object>
        {
            [nameof(ServerConfiguration.ObjectStorageUrlTtlHours)] = 48,
            [nameof(ServerConfiguration.ObjectStorageSslVerify)] = false,
        };

        Assert.That(
            ObjectStorageCliMode.ShouldSkipConsoleManaged(ExternalConsoleConfig(), pending, out _),
            Is.False);
    }

    [Test]
    public void DoesNotSkip_WhenStorageIsInternalMode()
    {
        var current = ExternalConsoleConfig();
        current.ObjectStorageInternal = true;

        var pending = new Dictionary<string, object>
        {
            [nameof(ServerConfiguration.ObjectStorageEndpoint)] = "https://outro.exemplo.com",
        };

        Assert.That(ObjectStorageCliMode.ShouldSkipConsoleManaged(current, pending, out _), Is.False);
    }

    [Test]
    public void DoesNotSkip_WhenExternalConfigIsIncomplete()
    {
        var current = ExternalConsoleConfig();
        current.ObjectStorageAccessKey = string.Empty; // console ainda não terminou a migração

        var pending = new Dictionary<string, object>
        {
            [nameof(ServerConfiguration.ObjectStorageAccessKey)] = "discoveryrustfs",
        };

        Assert.That(ObjectStorageCliMode.ShouldSkipConsoleManaged(current, pending, out _), Is.False);
    }

    [Test]
    public void IsConsoleManagedExternal_RequiresSecretAndEndpoint()
    {
        Assert.That(ObjectStorageCliMode.IsConsoleManagedExternal(ExternalConsoleConfig()), Is.True);

        var withoutSecret = ExternalConsoleConfig();
        withoutSecret.ObjectStorageSecretKey = string.Empty;
        Assert.That(ObjectStorageCliMode.IsConsoleManagedExternal(withoutSecret), Is.False);

        var internalMode = ExternalConsoleConfig();
        internalMode.ObjectStorageInternal = true;
        Assert.That(ObjectStorageCliMode.IsConsoleManagedExternal(internalMode), Is.False);
    }
}
