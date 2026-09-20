using Discovery.Core.Helpers;

namespace Discovery.Tests;

public class AgentVersionNormalizerTests
{
    [Test]
    public void IsPlaceholder_ShouldFlagDefaultsOfBuildsWithoutLdflags()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AgentVersionNormalizer.IsPlaceholder(null), Is.True);
            Assert.That(AgentVersionNormalizer.IsPlaceholder(""), Is.True);
            Assert.That(AgentVersionNormalizer.IsPlaceholder("   "), Is.True);
            Assert.That(AgentVersionNormalizer.IsPlaceholder("dev"), Is.True);
            Assert.That(AgentVersionNormalizer.IsPlaceholder("DEV"), Is.True);
            Assert.That(AgentVersionNormalizer.IsPlaceholder("unknown"), Is.True);
            Assert.That(AgentVersionNormalizer.IsPlaceholder("0.0.0"), Is.True);
            Assert.That(AgentVersionNormalizer.IsPlaceholder("1.2.1"), Is.False);
            Assert.That(AgentVersionNormalizer.IsPlaceholder("1.2.1-beta.1"), Is.False);
        });
    }

    [Test]
    public void PickVersion_ShouldPreferFirstMeaningfulCandidate()
    {
        // Heartbeat "0.0.0" (build local sem ldflags) não ofusca a versão
        // real já persistida pelo hardware report.
        Assert.That(AgentVersionNormalizer.PickVersion("0.0.0", "1.2.1"), Is.EqualTo("1.2.1"));

        // Heartbeat fresco (pós self-update) vence a entidade defasada.
        Assert.That(AgentVersionNormalizer.PickVersion("1.3.0", "1.2.1"), Is.EqualTo("1.3.0"));

        // Todos placeholders → null (o DTO exibe "—").
        Assert.That(AgentVersionNormalizer.PickVersion(null, "dev"), Is.Null);
        Assert.That(AgentVersionNormalizer.PickVersion(" unknown "), Is.Null);

        // Placeholder é ignorado mesmo com espaços; valor real é aparado.
        Assert.That(AgentVersionNormalizer.PickVersion("  1.2.1  "), Is.EqualTo("1.2.1"));
    }

    [Test]
    public void NormalizeCommit_ShouldNullPlaceholdersAndTrimRealValues()
    {
        Assert.That(AgentVersionNormalizer.NormalizeCommit(null), Is.Null);
        Assert.That(AgentVersionNormalizer.NormalizeCommit(""), Is.Null);
        Assert.That(AgentVersionNormalizer.NormalizeCommit("unknown"), Is.Null);
        Assert.That(AgentVersionNormalizer.NormalizeCommit("dev"), Is.Null);
        Assert.That(AgentVersionNormalizer.NormalizeCommit("  2d28aa0c  "), Is.EqualTo("2d28aa0c"));
    }
}
