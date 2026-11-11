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
    public void ThirdPartyOriginIsDisabledByDefault()
    {
        // D22 (visibilidade/report): a origem de terceiros vem DESATIVADA por
        // padrão. Ausente/inválido = só "os" — apps choco/winget não aparecem
        // nas visões de frota, não contam em conformidade e o agente não os
        // coleta. Este gate é de VISIBILIDADE: independe do 2º consentimento.
        Assert.That(PatchSourcePolicy.AllowsThirdParty(null), Is.False,
            "sem política a origem de terceiros é desativada");
        Assert.That(PatchSourcePolicy.AllowsThirdParty("[]"), Is.False);
        Assert.That(PatchSourcePolicy.AllowsThirdParty("[\"os\"]"), Is.False);
        Assert.That(PatchSourcePolicy.AllowsThirdParty("[\"os\",\"third_party\"]"), Is.True);
        Assert.That(PatchSourcePolicy.AllowsThirdParty("[\"THIRD_PARTY\"]"), Is.True,
            "aceite case-insensitive do valor gravado");
        Assert.That(PatchSourcePolicy.AllowsThirdParty("[\"inventado\"]"), Is.False,
            "valor desconhecido não habilita");
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

    [Test]
    public void DoesNotClassifyMsiAsThirdPartySource()
    {
        // "msi" era a origem do catálogo próprio de download direto (WU-21).
        // Sem catálogo curado, a origem não existe mais: aceitá-la aqui faria o
        // consentimento de terceiros valer para uma origem que ninguém serve.
        Assert.That(PatchSourcePolicy.IsThirdPartySource("msi"), Is.False);
        Assert.That(PatchSourcePolicy.IsThirdPartySource("MSI"), Is.False);
    }
}
