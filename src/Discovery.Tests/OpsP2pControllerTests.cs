using Discovery.Api.Controllers;

namespace Discovery.Tests;

/// <summary>
/// Blindagem do parâmetro windowHours dos endpoints /ops/p2p/*: valores absurdos
/// causavam OverflowException (HTTP 500) em TimeSpan.FromHours/DateTime.AddHours.
/// </summary>
public class OpsP2pControllerTests
{
    [TestCase(0, 1)]
    [TestCase(-5, 1)]
    [TestCase(1, 1)]
    [TestCase(24, 24)]
    [TestCase(168, 168)]
    [TestCase(720, 720)]
    [TestCase(721, 720)]
    [TestCase(100000, 720)]
    [TestCase(int.MaxValue, 720)]
    public void NormalizeWindowHours_ClampsToSupportedRange(int input, int expected)
    {
        Assert.That(OpsP2pController.NormalizeWindowHours(input), Is.EqualTo(expected));
    }
}
