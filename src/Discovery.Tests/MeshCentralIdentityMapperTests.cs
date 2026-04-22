using Discovery.Core.Entities.Identity;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

public class MeshCentralIdentityMapperTests
{
    [Test]
    public void ResolveProvisioningUsername_ShouldPreferPersistedUsername()
    {
        var mapper = new MeshCentralIdentityMapper();
        var user = new User
        {
            Id = Guid.NewGuid(),
            MeshCentralUsername = "Legacy_User"
        };

        var username = mapper.ResolveProvisioningUsername(user);

        Assert.That(username, Is.EqualTo("legacy_user"));
    }

    [Test]
    public void ResolveProvisioningUsername_ShouldUseStableIdBasedUsername_WhenUserHasNoMeshUsername()
    {
        var mapper = new MeshCentralIdentityMapper();
        var userId = Guid.Parse("d4db53d1-3a65-4203-b5ca-3f22462999ff");
        var user = new User
        {
            Id = userId
        };

        var username = mapper.ResolveProvisioningUsername(user);

        Assert.That(username, Is.EqualTo("mdz-d4db53d13a654203b5ca3f22462999ff"));
    }
}