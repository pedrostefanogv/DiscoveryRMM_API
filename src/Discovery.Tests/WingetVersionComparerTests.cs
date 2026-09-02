using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Testes do comparador tolerante de versões Winget (server-side).
/// </summary>
public class WingetVersionComparerTests
{
    [TestCase("2026.1.3", "2026.1.3", 0)]
    [TestCase("2026.1.3", "2026.1.3.36551", -1)]   // ausente = 0 → menor
    [TestCase("2026.1.3.36551", "2026.1.3", 1)]
    [TestCase("132.0.1", "132.0", 1)]
    [TestCase("1.29.289.0", "1.30", -1)]
    [TestCase("2026.2", "2026.1.9", 1)]
    [TestCase("10", "9.9.9", 1)]
    [TestCase("v1.2.3", "1.2.3", 0)]                // prefixo v ignorado
    [TestCase("2026.2-beta", "2026.1.9", 1)]        // beta de 2026.2 > 2026.1.9
    [TestCase("2026.2-beta", "2026.2", -1)]         // pré-release < estável
    [TestCase("2026.2-rc1", "2026.2-beta", 1)]      // rc > beta
    [TestCase("1.0b12345", "1.0", 1)]               // sufixo build > estável
    [TestCase("1.0b12345", "1.0b12346", -1)]
    [TestCase("1.0r2", "1.0r1", 1)]
    public void Compare_KnownCases_ReturnsExpectedOrdering(string a, string b, int expectedSign)
    {
        var result = Math.Sign(WingetVersionComparer.Compare(a, b));

        Assert.That(result, Is.EqualTo(expectedSign));
    }

    [Test]
    public void Compare_NullOrEmpty_IsAlwaysSmaller()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WingetVersionComparer.Compare(null, "1.0"), Is.Negative);
            Assert.That(WingetVersionComparer.Compare("", "1.0"), Is.Negative);
            Assert.That(WingetVersionComparer.Compare("1.0", null), Is.Positive);
            Assert.That(WingetVersionComparer.Compare(null, null), Is.Zero);
            Assert.That(WingetVersionComparer.Compare("", ""), Is.Zero);
        });
    }

    [Test]
    public void Compare_GarbageStrings_DoesNotThrowAndIsStable()
    {
        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(() => WingetVersionComparer.Compare("!!??", "1.0"));
            Assert.DoesNotThrow(() => WingetVersionComparer.Compare("abc", "abc"));
            Assert.That(WingetVersionComparer.Compare("abc", "abc"), Is.Zero);
        });
    }

    [Test]
    public void IsNewer_CandidateNewer_ReturnsTrue()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WingetVersionComparer.IsNewer("2026.2.1", "2026.1.3"), Is.True);
            Assert.That(WingetVersionComparer.IsNewer("2026.1.3", "2026.2.1"), Is.False);
            Assert.That(WingetVersionComparer.IsNewer("2026.1.3", "2026.1.3"), Is.False);
            Assert.That(WingetVersionComparer.IsNewer(null, "2026.1.3"), Is.False);
            Assert.That(WingetVersionComparer.IsNewer("2026.1.3", null), Is.True);
        });
    }
}
