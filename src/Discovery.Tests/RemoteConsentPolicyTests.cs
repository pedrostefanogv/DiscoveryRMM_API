using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;

namespace Discovery.Tests;

public class RemoteConsentPolicyTests
{
    [Test]
    public void AppliesTo_only_desktop_kinds()
    {
        Assert.That(RemoteConsentPolicy.AppliesTo(RemoteSessionKind.Screen), Is.True);
        Assert.That(RemoteConsentPolicy.AppliesTo(RemoteSessionKind.All), Is.True);
        Assert.That(RemoteConsentPolicy.AppliesTo(RemoteSessionKind.Terminal), Is.False);
        Assert.That(RemoteConsentPolicy.AppliesTo(RemoteSessionKind.Files), Is.False);
        Assert.That(RemoteConsentPolicy.AppliesTo(RemoteSessionKind.Proxy), Is.False);
        Assert.That(RemoteConsentPolicy.AppliesTo(RemoteSessionKind.Processes), Is.False);
    }

    [Test]
    public void TopSource_prefers_most_specific_scope()
    {
        var policy = new RemoteConsentPolicy
        {
            Sources = new Dictionary<string, RemoteConsentSource>(StringComparer.OrdinalIgnoreCase)
            {
                ["Mode"] = new("Server"),
                ["TimeoutSeconds"] = new("Site"),
                ["Message"] = new("UserGroup")
            }
        };

        Assert.That(policy.TopSource(), Is.EqualTo("UserGroup"));
    }

    [Test]
    public void RequiresPrompt_only_in_prompt_mode()
    {
        Assert.That(new RemoteConsentPolicy { Mode = RemoteConsentMode.Prompt }.RequiresPrompt, Is.True);
        Assert.That(new RemoteConsentPolicy { Mode = RemoteConsentMode.NotifyOnly }.RequiresPrompt, Is.False);
        Assert.That(new RemoteConsentPolicy { Mode = RemoteConsentMode.Off }.RequiresPrompt, Is.False);
    }

    [Test]
    public void Prompt_defaults_require_explicit_block()
    {
        Assert.That(RemoteConsentPolicy.AppliesTo(RemoteSessionKind.Screen), Is.True);
        Assert.That(new RemoteConsentPolicy { Mode = RemoteConsentMode.Off }.RequiresPrompt, Is.False);
    }
}
