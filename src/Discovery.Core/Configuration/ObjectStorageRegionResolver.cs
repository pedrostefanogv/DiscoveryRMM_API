namespace Discovery.Core.Configuration;

/// <summary>
/// Resolve a região (SigV4) do object storage quando o operador NÃO informa uma.
///
/// Cloudflare R2 não tem regiões (o console da Cloudflare usa `auto`), Backblaze
/// B2 e DigitalOcean Spaces derivam a região do próprio host, e MinIO/RustFS
/// ignoram o valor. Antes, deixar o campo Região em branco fazia
/// `ObjectStorageSettings.Validate()` recusar a configuração inteira
/// ("Region é obrigatório") e TODO upload falhava — mesmo com endpoint, bucket e
/// chaves corretos (caso real: R2 no console).
///
/// A região também entra na assinatura SigV4 (scope de credencial), portanto ela
/// nunca pode ficar vazia: o SDK MinIO assume us-east-1, e o R2 espera `auto`.
/// </summary>
public static class ObjectStorageRegionResolver
{
    /// <summary>Default para provedores que ignoram o valor ou não o expõem.</summary>
    public const string DefaultRegion = "us-east-1";

    /// <summary>Valor usado pela Cloudflare R2 no lugar de uma região.</summary>
    public const string CloudflareR2Region = "auto";

    /// <summary>
    /// Região efetiva: a configurada (trim) quando existir; senão, derivada do
    /// endpoint; senão, <see cref="DefaultRegion"/>.
    /// </summary>
    public static string Resolve(string? configuredRegion, string? endpoint)
    {
        var configured = configuredRegion?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return FromEndpoint(endpoint);
    }

    /// <summary>Deriva a região do host do endpoint (R2/AWS/B2/Spaces/DO) ou cai no default.</summary>
    public static string FromEndpoint(string? endpoint)
    {
        var host = HostOf(endpoint);
        if (string.IsNullOrWhiteSpace(host))
            return DefaultRegion;

        // Cloudflare R2: <accountid>.r2.cloudflarestorage.com (sem região).
        if (host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase))
            return CloudflareR2Region;

        // AWS S3: s3[.-]<region>.amazonaws.com / <bucket>.s3.<region>.amazonaws.com
        // / s3.dualstack.<region>.amazonaws.com / s3.amazonaws.com (us-east-1).
        if (host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < parts.Length; i++)
            {
                if (parts[i].Equals("dualstack", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsAwsRegionToken(parts[i]))
                    return parts[i];
            }

            return DefaultRegion;
        }

        // Backblaze B2: s3.<region>.backblazeb2.com
        if (host.EndsWith(".backblazeb2.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals("s3", StringComparison.OrdinalIgnoreCase))
                return parts[1];
        }

        // DigitalOcean Spaces: <region>.digitaloceanspaces.com / s3.<region>.digitaloceanspaces.com
        if (host.EndsWith(".digitaloceanspaces.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("digitaloceanspaces.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return parts[0].Equals("s3", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3
                    ? parts[1]
                    : parts[0];
        }

        return DefaultRegion;
    }

    /// <summary>Token com cara de região AWS (us-east-1, sa-east-1, eu-central-2, us-gov-west-1).</summary>
    private static bool IsAwsRegionToken(string token)
        => token.Length > 3
           && token.Contains('-')
           && char.IsLetter(token[0])
           && !token.Equals("amazonaws", StringComparison.OrdinalIgnoreCase);

    /// <summary>Host minúsculo do endpoint, sem esquema, porta, path ou credenciais.</summary>
    private static string HostOf(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return string.Empty;

        var raw = endpoint.Trim();
        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = "https://" + raw;

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host.ToLowerInvariant()
            : string.Empty;
    }
}
