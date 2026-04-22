namespace Discovery.Core.Interfaces;

public interface IMeshCentralTokenService
{
    string GenerateControlAuthToken(string username);
    string GenerateLoginToken(string username);
}