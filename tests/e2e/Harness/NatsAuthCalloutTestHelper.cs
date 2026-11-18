using System.Text;
using System.Text.Json;
using NATS.Jwt;
using NATS.Jwt.Models;
using NATS.NKeys;

namespace Discovery.E2E.Tests.Harness;

public sealed record DecodedNatsAuthResponse(string? Jwt, string? Error, string Issuer, string Subject, string Audience);

public static class NatsAuthCalloutTestHelper
{
    public static string BuildSignedAuthRequestJwt(
        KeyPair serverKeyPair,
        string userNkey,
        string? authToken = null,
        string? token = null,
        string? jwt = null,
        string? overrideServerId = null)
    {
        var serverPub = overrideServerId ?? serverKeyPair.GetPublicKey();
        var claims = NatsJwt.NewAuthorizationRequestClaims(userNkey);
        claims.Issuer = serverPub;
        claims.Audience = "$G";
        claims.AuthorizationRequest.NatsServer = new NatsServerId
        {
            Id = serverPub,
            Name = "fake-e2e-nats",
            Version = "2.10.0"
        };
        claims.AuthorizationRequest.UserNKey = userNkey;
        claims.AuthorizationRequest.NatsConnectOptions = new NatsConnectOptions
        {
            Protocol = 1,
            Token = authToken ?? token ?? string.Empty,
            Jwt = jwt ?? string.Empty
        };

        return NatsJwt.EncodeAuthorizationRequestClaims(claims, serverKeyPair);
    }

    public static string BuildPreIssuedUserJwt(
        KeyPair accountKeyPair,
        string userNkey,
        string[] pubAllow,
        string[] subAllow,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? notBefore = null,
        string name = "session:e2e")
    {
        var claims = NatsJwt.NewUserClaims(userNkey);
        claims.Name = name;
        claims.Audience = "$G";
        claims.Expires = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30);
        if (notBefore.HasValue)
            claims.NotBefore = notBefore.Value;
        claims.User.Pub.Allow = pubAllow.ToList();
        claims.User.Sub.Allow = subAllow.ToList();
        return NatsJwt.EncodeUserClaims(claims, accountKeyPair);
    }

    public static DecodedNatsAuthResponse DecodeAuthResponse(string responseJwt, KeyPair? verifyAccountKeyPair = null)
    {
        var parts = responseJwt.Split('.');
        if (parts.Length != 3)
            throw new InvalidDataException($"Expected 3-part NATS JWT, got: {responseJwt}");

        if (verifyAccountKeyPair is not null)
        {
            var sig = Base64UrlDecode(parts[2]);
            var input = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            if (!verifyAccountKeyPair.Verify(input, sig))
                throw new InvalidDataException("Response JWT Ed25519 signature verification failed.");
        }

        using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        var root = doc.RootElement;
        var nats = root.GetProperty("nats");
        var jwt = nats.TryGetProperty("jwt", out var jwtEl) && jwtEl.ValueKind == JsonValueKind.String ? jwtEl.GetString() : null;
        var error = nats.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String ? errEl.GetString() : null;
        var iss = root.TryGetProperty("iss", out var issEl) ? issEl.GetString() ?? "" : "";
        var sub = root.TryGetProperty("sub", out var subEl) ? subEl.GetString() ?? "" : "";
        var aud = root.TryGetProperty("aud", out var audEl) ? audEl.GetString() ?? "" : "";
        return new DecodedNatsAuthResponse(jwt, error, iss, sub, aud);
    }

    public static (string Subject, string Issuer, string[] PubAllow, string[] SubAllow) DecodeUserJwt(string userJwt, KeyPair verifyAccountKeyPair)
    {
        var parts = userJwt.Split('.');
        if (parts.Length != 3)
            throw new InvalidDataException("Invalid 3-part NATS user JWT.");

        var sig = Base64UrlDecode(parts[2]);
        var input = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        if (!verifyAccountKeyPair.Verify(input, sig))
            throw new InvalidDataException("User JWT Ed25519 signature verification failed.");

        using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        var root = doc.RootElement;
        var sub = root.GetProperty("sub").GetString() ?? "";
        var iss = root.GetProperty("iss").GetString() ?? "";
        var nats = root.GetProperty("nats");

        string[] ReadAllow(string section)
        {
            if (nats.TryGetProperty(section, out var secEl)
                && secEl.TryGetProperty("allow", out var allowEl)
                && allowEl.ValueKind == JsonValueKind.Array)
            {
                return allowEl.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
            }
            return [];
        }

        return (sub, iss, ReadAllow("pub"), ReadAllow("sub"));
    }

    public static byte[] Base64UrlDecode(string input)
    {
        var b64 = input.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
        }
        return Convert.FromBase64String(b64);
    }

    public static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
