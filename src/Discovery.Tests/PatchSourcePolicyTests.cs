using Discovery.Core.Helpers;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class PatchSourcePolicyTests
{
    [Test]
    public void MissingSourcesMeansOsOnly()
    {
        Assert.That(PatchSourcePolicy.AllowsOs(null), Is.True);
        Assert.That(PatchSourcePolicy.AllowsOs("[]"), Is.True);
        Assert.That(PatchSourcePolicy.ThirdPartyConsented(null, allowThirdPartyApps: true), Is.False,
            "sem sources nao ha consentimento para terceiros, mesmo com flag ligada");
    }

    [Test]
    public void ThirdPartyNeedsBothSwitches()
    {
        const string withThirdParty = "[\"os\",\"third_party\"]";
        Assert.That(PatchSourcePolicy.ThirdPartyConsented(withThirdParty, allowThirdPartyApps: false), Is.False,
            "flag desligada bloqueia");
        Assert.That(PatchSourcePolicy.ThirdPartyConsented(withThirdParty, allowThirdPartyApps: true), Is.True);

        const string osOnly = "[\"os\"]";
        Assert.That(PatchSourcePolicy.ThirdPartyConsented(osOnly, allowThirdPartyApps: true), Is.False,
            "sources sem third_party bloqueia mesmo com flag ligada");
    }

    [Test]
    public void UnknownSourcesAreIgnored()
    {
        var effective = PatchSourcePolicy.EffectiveSources("[\"os\",\"inventado\"]");
        Assert.That(effective, Is.EquivalentTo(new[] { "os" }));
    }

    [Test]
    public void ThirdPartyIsExemptFromPublicationAgeGate()
    {
        // Documenta a regra do resolver: deferral por idade (D09) é de OS;
        // terceiros usam consentimento (D22), senão ficariam retidos para sempre
        // por não terem PublishedAt.
        Assert.That(PatchSourcePolicy.IsThirdPartySource("choco"), Is.True);
        Assert.That(PatchSourcePolicy.IsThirdPartySource("wua"), Is.False);
    }

    [Test]
    public void ClassifiesWaaSFeatureUpgradeSource()
    {
        Assert.That(PatchSourcePolicy.IsWaaSFeatureSource("waas"), Is.True);
        Assert.That(PatchSourcePolicy.IsWaaSFeatureSource("WAAS"), Is.True);
        Assert.That(PatchSourcePolicy.IsWaaSFeatureSource("wua"), Is.False);
        Assert.That(PatchSourcePolicy.IsWaaSFeatureSource(null), Is.False);
    }

    [Test]
    public void ClassifiesThirdPartySources()
    {
        Assert.That(PatchSourcePolicy.IsThirdPartySource("choco"), Is.True);
        Assert.That(PatchSourcePolicy.IsThirdPartySource("WINGET"), Is.True);
        Assert.That(PatchSourcePolicy.IsThirdPartySource("wua"), Is.False);
        Assert.That(PatchSourcePolicy.IsThirdPartySource(null), Is.False);
    }
}
