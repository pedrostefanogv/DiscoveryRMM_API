namespace Discovery.Core.Interfaces;

public interface IMeshCentralConfigService
{
    string GetPublicBaseUrl();
    string GetAdministrativeBaseUrl();
    string GetTechnicalUsername();
    Uri BuildControlWebSocketUri(string authToken);
}