using Discovery.Core.Cqrs.Patches;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class PatchUpdateKeyValidatorTests
{
    [TestCase("guid:3c3f1f1a-1111-2222-3333-444455556666:12")]
    [TestCase("guid:3c3f1f1a-1111-2222-3333-444455556666:0")]
    [TestCase("kb:KB5034441")]
    [TestCase("KB:kb5034441")]
    public void AcceptsCanonicalKeys(string key)
    {
        Assert.That(PatchUpdateKeyValidator.IsValid(key), Is.True, key);
    }

    [TestCase("")]
    [TestCase("kb:")]
    [TestCase("kb:ABC")]
    [TestCase("kb:KB12")]
    [TestCase("guid:abc:1")]
    [TestCase("guid:3c3f1f1a-1111-2222-3333-444455556666")]
    [TestCase("3c3f1f1a-1111-2222-3333-444455556666:1")]
    [TestCase("kb:KB5034441:1")]
    public void RejectsInvalidKeys(string key)
    {
        Assert.That(PatchUpdateKeyValidator.IsValid(key), Is.False, key);
    }

    [Test]
    public void NormalizesCase()
    {
        Assert.That(PatchUpdateKeyValidator.Normalize("GUID:3C3F1F1A-1111-2222-3333-444455556666:12"),
            Is.EqualTo("guid:3c3f1f1a-1111-2222-3333-444455556666:12"));
        Assert.That(PatchUpdateKeyValidator.Normalize("KB:kb5034441"), Is.EqualTo("kb:KB5034441"));
    }
}
