using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;

namespace Discovery.Tests;

/// <summary>
/// Regressões de PATCH: chaves inexistentes/legadas precisam ser rejeitadas em
/// vez de ignoradas silenciosamente.
/// </summary>
public class ConfigurationPatchValidatorTests
{
    [Test]
    public void FindUnknownFields_RejectsLegacyMeshCentralFields()
    {
        var unknown = ConfigurationPatchValidator.FindUnknownFields(
            typeof(ServerConfiguration),
            ["meshCentralGroupPolicyProfile", "tokenExpirationDays", "agentOfflineThresholdSeconds"]);

        Assert.That(unknown.Count, Is.EqualTo(3));
    }

    [Test]
    public void FindUnknownFields_AcceptsRealServerFields()
    {
        var unknown = ConfigurationPatchValidator.FindUnknownFields(
            typeof(ServerConfiguration),
            ["discoveryEnabled", "agentOnlineGraceSeconds", "natsServerHostExternal", "ticketAttachmentSettingsJson"]);

        Assert.That(unknown, Is.Empty);
    }

    [Test]
    public void FindUnknownFields_AcceptsRealClientFields()
    {
        var unknown = ConfigurationPatchValidator.FindUnknownFields(
            typeof(ClientConfiguration),
            ["recoveryEnabled", "agentOnlineGraceSeconds", "lockedFieldsJson"]);

        Assert.That(unknown, Is.Empty);
    }

    [Test]
    public void FindUnknownFields_RejectsAuditAndReadOnlyFields()
    {
        var unknown = ConfigurationPatchValidator.FindUnknownFields(
            typeof(ServerConfiguration),
            ["id", "version", "createdAt", "objectStorageSecretKeyConfigured"]);

        Assert.That(unknown.Count, Is.EqualTo(4));
    }

    [Test]
    public void FindUnknownFields_RejectsUnknownRoleField()
    {
        var unknown = ConfigurationPatchValidator.FindUnknownFields(
            typeof(Role),
            ["meshRightsMask", "meshRightsProfile"]);

        Assert.That(unknown.Count, Is.EqualTo(2));
    }

    [Test]
    public void EnsureKnownFields_ThrowsWithFieldList()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ConfigurationPatchValidator.EnsureKnownFields(
                typeof(ServerConfiguration),
                ["meshCentralGroupPolicyProfile"],
                "Server"));

        Assert.That(error!.Message, Does.Contain("meshCentralGroupPolicyProfile"));
    }
}
