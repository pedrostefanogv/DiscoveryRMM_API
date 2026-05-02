using Microsoft.AspNetCore.SignalR;

namespace Discovery.Api.Hubs;

/// <summary>
/// Helper para popular o HubCallerContext.Items a partir do HttpContext.Items,
/// fazendo a ponte entre os middlewares de autenticação e os Hubs SignalR.
/// </summary>
public static class HubAuthBridge
{
    private static readonly string[] KeysToBridge =
    [
        "UserId",
        "AgentId",
        "TokenId",
        "IsApiTokenAuth",
        "AgentTlsCertHash",
        "HandshakeState",
        "ConfirmedTlsFingerprint",
        "MfaPending",
        "MfaSetup",
        "SessionId",
    ];

    public static void BridgeHttpContextItems(this HubCallerContext context)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext is null) return;

        foreach (var key in KeysToBridge)
        {
            if (httpContext.Items.TryGetValue(key, out var value) && value is not null)
            {
                context.Items[key] = value;
            }
        }
    }
}
