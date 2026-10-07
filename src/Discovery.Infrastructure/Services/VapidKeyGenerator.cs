using System.Security.Cryptography;

namespace Discovery.Infrastructure.Services;

/// <summary>Par de chaves VAPID em base64url (formato exigido pelos provedores).</summary>
public sealed record VapidKeyPair(string PublicKey, string PrivateKey);

/// <summary>
/// Gera um par VAPID P-256: chave privada = escalar D; chave publica = ponto
/// nao comprimido (0x04 || X || Y), ambas em base64url sem padding.
/// </summary>
public static class VapidKeyGenerator
{
    public static VapidKeyPair Generate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: true);

        var publicPoint = new byte[65];
        publicPoint[0] = 0x04;
        WritePadded(parameters.Q.X, publicPoint.AsSpan(1, 32));
        WritePadded(parameters.Q.Y, publicPoint.AsSpan(33, 32));

        var privateKey = parameters.D ?? throw new CryptographicException("Chave privada indisponivel.");
        return new VapidKeyPair(Base64UrlEncode(publicPoint), Base64UrlEncode(privateKey));
    }

    private static void WritePadded(byte[]? source, Span<byte> destination)
    {
        destination.Clear();
        if (source is null || source.Length == 0)
            return;

        source.CopyTo(destination[^source.Length..]);
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
