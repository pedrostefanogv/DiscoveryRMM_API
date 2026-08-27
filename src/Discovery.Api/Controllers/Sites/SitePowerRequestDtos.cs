using System.Text.Json.Serialization;

namespace Discovery.Api.Controllers.Sites;

public record SiteRestartRequest(
    [property: JsonPropertyName("delaySeconds")] int DelaySeconds = 15,
    [property: JsonPropertyName("force")] bool Force = false,
    [property: JsonPropertyName("message")] string? Message = null);

public record SiteShutdownRequest(
    [property: JsonPropertyName("delaySeconds")] int DelaySeconds = 30,
    [property: JsonPropertyName("force")] bool Force = false,
    [property: JsonPropertyName("message")] string? Message = null);