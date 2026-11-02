using Discovery.Core.Configuration;
using Discovery.Core.ValueObjects;

namespace Discovery.Tests;

/// <summary>
/// Regressão do caso Cloudflare R2: o console não pede região (R2 usa "auto"),
/// e antes `ObjectStorageSettings.Validate()` recusava a configuração inteira
/// ("Region é obrigatório") — todo upload de anexo falhava mesmo com endpoint,
/// bucket e chaves válidos.
/// </summary>
[TestFixture]
public class ObjectStorageRegionResolverTests
{
    [Test]
    public void BlankRegion_IsAcceptedByValidation()
    {
        var settings = new ObjectStorageSettings
        {
            BucketName = "discovery-rmm",
            Endpoint = "https://700248b3378244fc7abb58767ffcen.r2.cloudflarestorage.com",
            Region = string.Empty,
            AccessKey = "key",
            SecretKey = "secret",
        };

        var errors = settings.Validate();

        Assert.That(errors, Is.Empty, string.Join("; ", errors));
    }

    [Test]
    public void ConfiguredRegion_Wins()
    {
        Assert.That(
            ObjectStorageRegionResolver.Resolve("eu-west-1", "https://s3.us-east-1.amazonaws.com"),
            Is.EqualTo("eu-west-1"));
        Assert.That(
            ObjectStorageRegionResolver.Resolve("  sa-east-1  ", null),
            Is.EqualTo("sa-east-1"));
    }

    [TestCase("https://700248b3378244fc7abb58767ffcf230.r2.cloudflarestorage.com", "auto")]
    [TestCase("700248b3378244fc7abb58767ffcf230.r2.cloudflarestorage.com", "auto")]
    [TestCase("https://s3.sa-east-1.amazonaws.com", "sa-east-1")]
    [TestCase("s3.us-east-1.amazonaws.com", "us-east-1")]
    [TestCase("s3.amazonaws.com", "us-east-1")]
    [TestCase("s3.dualstack.eu-central-1.amazonaws.com", "eu-central-1")]
    [TestCase("https://s3.us-west-004.backblazeb2.com", "us-west-004")]
    [TestCase("https://nyc3.digitaloceanspaces.com", "nyc3")]
    [TestCase("http://127.0.0.1:9000", "us-east-1")]
    [TestCase("https://storage.example.com", "us-east-1")]
    [TestCase(null, "us-east-1")]
    [TestCase("", "us-east-1")]
    public void RegionIsDerivedFromEndpoint(string? endpoint, string expected)
        => Assert.That(ObjectStorageRegionResolver.Resolve(null, endpoint), Is.EqualTo(expected));

    [Test]
    public void ProviderUsesResolvedRegion_InsteadOfThrowing()
    {
        // O Provider CAI no default quando a região está vazia; o que impedia o
        // upload era a validação. Garante que a resolução nunca devolve vazio.
        var settings = new ObjectStorageSettings
        {
            BucketName = "b",
            Endpoint = "https://x.r2.cloudflarestorage.com",
            Region = ObjectStorageRegionResolver.Resolve(null, "https://x.r2.cloudflarestorage.com"),
            AccessKey = "k",
            SecretKey = "s",
        };

        Assert.That(settings.Region, Is.EqualTo("auto"));
        Assert.That(settings.Validate(), Is.Empty);
    }

    [Test]
    public void RegionWithSpace_IsStillRejected()
    {
        var settings = new ObjectStorageSettings
        {
            BucketName = "b",
            Endpoint = "https://s3.amazonaws.com",
            Region = "us east 1",
            AccessKey = "k",
            SecretKey = "s",
        };

        Assert.That(settings.Validate(), Has.Some.Contains("Region"));
    }
}
