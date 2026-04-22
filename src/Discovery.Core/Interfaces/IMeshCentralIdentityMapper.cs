using Discovery.Core.Entities.Identity;

namespace Discovery.Core.Interfaces;

public interface IMeshCentralIdentityMapper
{
    string ResolveProvisioningUsername(User user);
    string SuggestUsername(string localUsername, Guid? userId = null);
}