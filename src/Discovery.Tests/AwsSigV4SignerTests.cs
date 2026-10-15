using Discovery.Core.Helpers;

namespace Discovery.Tests;

/// <summary>
/// A assinatura SigV4 é o único caminho para pré-assinar PARTES de um upload
/// multipart (o SDK MinIO 7.0.0 não tem presign de parte nem abort). Um erro
/// aqui faria o storage recusar cada parte — por isso o teste ancora no vetor
/// publicado pela AWS, e não em valores gerados pela própria implementação.
/// </summary>
public class AwsSigV4SignerTests
{
    // Exemplo oficial da AWS (Authenticating Requests: Using Query Parameters).
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string ExpectedSignature =
        "aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404";

    [Test]
    public void PresignedUrl_MatchesAwsDocumentedExample()
    {
        var url = AwsSigV4Signer.CreatePresignedUrl(
            "GET",
            new Uri("https://examplebucket.s3.amazonaws.com"),
            "/test.txt",
            query: null,
            accessKey: AccessKey,
            secretKey: SecretKey,
            region: "us-east-1",
            now: new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero),
            expiresSeconds: 86400);

        Assert.That(url, Does.Contain("X-Amz-Signature=" + ExpectedSignature));
        Assert.That(url, Does.StartWith("https://examplebucket.s3.amazonaws.com/test.txt?"));
        Assert.That(url, Does.Contain("X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request"));
        Assert.That(url, Does.Contain("X-Amz-Expires=86400"));
        Assert.That(url, Does.Contain("X-Amz-SignedHeaders=host"));
    }

    [Test]
    public void PresignedPartUrl_CarriesPartNumberAndUploadId_AndIsSigned()
    {
        var url = AwsSigV4Signer.CreatePresignedPartUrl(
            new Uri("https://tngplacas.com.br"),
            "discovery-files",
            "global/ticket/abc/attachments/backup.iso",
            uploadId: "Vf0mZ0c0EXAMPLE",
            partNumber: 7,
            accessKey: AccessKey,
            secretKey: SecretKey,
            region: "us-east-1",
            now: new DateTimeOffset(2026, 10, 11, 22, 0, 0, TimeSpan.Zero),
            expiresSeconds: 3600);

        // Path-style (obrigatório atrás do nginx que preserva o Host).
        Assert.That(url, Does.StartWith("https://tngplacas.com.br/discovery-files/global/ticket/abc/attachments/backup.iso?"));
        Assert.That(url, Does.Contain("partNumber=7"));
        Assert.That(url, Does.Contain("uploadId=Vf0mZ0c0EXAMPLE"));
        Assert.That(url, Does.Contain("X-Amz-Signature="));
        Assert.That(url, Does.Contain("X-Amz-Expires=3600"));
    }

    [Test]
    public void PresignedObjectUrl_InitiateMultipart_CarriesUploadsFlag()
    {
        var url = AwsSigV4Signer.CreatePresignedObjectUrl(
            "POST",
            new Uri("http://127.0.0.1:9000"),
            "discovery-files",
            "app-store/custom/installers/abc123/setup.exe",
            new Dictionary<string, string> { ["uploads"] = string.Empty },
            AccessKey, SecretKey, "us-east-1",
            new DateTimeOffset(2026, 10, 11, 22, 0, 0, TimeSpan.Zero),
            expiresSeconds: 300);

        // Initiate/complete/abort saem do SERVIDOR para o endpoint interno.
        Assert.That(url, Does.StartWith("http://127.0.0.1:9000/discovery-files/app-store/custom/installers/abc123/setup.exe?"));
        Assert.That(url, Does.Contain("uploads="));
        Assert.That(url, Does.Contain("X-Amz-Signature="));
    }

    [Test]
    public void PresignedObjectUrl_AbortMultipart_CarriesUploadId()
    {
        var url = AwsSigV4Signer.CreatePresignedObjectUrl(
            "DELETE",
            new Uri("http://127.0.0.1:9000"),
            "discovery-files",
            "app-store/custom/installers/abc123/setup.exe",
            new Dictionary<string, string> { ["uploadId"] = "Vf0mZ0c0EXAMPLE" },
            AccessKey, SecretKey, "us-east-1",
            new DateTimeOffset(2026, 10, 11, 22, 0, 0, TimeSpan.Zero),
            expiresSeconds: 300);

        Assert.That(url, Does.Contain("uploadId=Vf0mZ0c0EXAMPLE"));
        Assert.That(url, Does.Contain("X-Amz-Signature="));
    }

    [Test]
    public void PresignedListPartsUrl_IsReadOnlyAndCarriesUploadId()
    {
        var url = AwsSigV4Signer.CreatePresignedListPartsUrl(
            new Uri("http://127.0.0.1:9000"),
            "discovery-files",
            "app-store/custom/installers/abc123/setup.exe",
            "Vf0mZ0c0EXAMPLE",
            AccessKey, SecretKey, "us-east-1",
            new DateTimeOffset(2026, 10, 11, 22, 0, 0, TimeSpan.Zero),
            expiresSeconds: 300);

        Assert.That(url, Does.Contain("uploadId=Vf0mZ0c0EXAMPLE"));
        Assert.That(url, Does.Not.Contain("partNumber="));
        Assert.That(url, Does.Contain("X-Amz-Expires=300"));
    }

    [Test]
    public void PresignedObjectUrl_EncodesKeySegments_KeepingSlashes()
    {
        var url = AwsSigV4Signer.CreatePresignedObjectUrl(
            "PUT",
            new Uri("https://storage.example.com"),
            "discovery-files",
            "app-store/custom/installers/abc 123/meu setup v2.exe",
            new Dictionary<string, string> { ["partNumber"] = "1", ["uploadId"] = "u1" },
            AccessKey, SecretKey, "us-east-1",
            new DateTimeOffset(2026, 10, 11, 22, 0, 0, TimeSpan.Zero),
            expiresSeconds: 3600);

        Assert.That(
            url,
            Does.StartWith("https://storage.example.com/discovery-files/app-store/custom/installers/abc%20123/meu%20setup%20v2.exe?"));
    }

    [Test]
    public void PresignedPartUrl_EscapaCaracteresQueOdotNetNaoEscapa()
    {
        // '!' '*' ''' '(' ')' são unreserved para Uri.EscapeDataString, mas o SigV4
        // (AWS UriEncode) exige percent-encoding. Sem isso, um instalador chamado
        // "setup(1).exe" geraria assinatura divergente e o storage recusaria a URL.
        var url = AwsSigV4Signer.CreatePresignedPartUrl(
            new Uri("https://tngplacas.com.br"),
            "discovery-files",
            "app-store/custom/installers/abc/setup(1)*final!.exe",
            uploadId: "u+id/=",
            partNumber: 1,
            accessKey: AccessKey, secretKey: SecretKey, region: "us-east-1",
            now: new DateTimeOffset(2026, 10, 11, 22, 0, 0, TimeSpan.Zero),
            expiresSeconds: 3600);

        Assert.That(url, Does.Contain("setup%281%29%2Afinal%21.exe"), "path precisa escapar ()*!");
        Assert.That(url, Does.Not.Contain("setup(1)"));
        Assert.That(url, Does.Contain("uploadId=u%2Bid%2F%3D"), "query precisa escapar + e /");
    }

    [Test]
    public void PresignedObjectUrl_RejectsEmptyBucket()
    {
        Assert.Throws<ArgumentException>(() => AwsSigV4Signer.CreatePresignedObjectUrl(
            "POST", new Uri("https://s3.example.com"), " ", "k", null,
            AccessKey, SecretKey, "us-east-1", DateTimeOffset.UtcNow, 60));
    }

    [Test]
    public void PresignedPartUrl_RejectsInvalidPartNumber()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AwsSigV4Signer.CreatePresignedPartUrl(
            new Uri("https://tngplacas.com.br"), "b", "k", "u", partNumber: 0,
            accessKey: AccessKey, secretKey: SecretKey, region: "us-east-1",
            now: DateTimeOffset.UtcNow, expiresSeconds: 60));
    }
}
