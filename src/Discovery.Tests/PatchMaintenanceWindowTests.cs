using Discovery.Core.Helpers;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class PatchMaintenanceWindowTests
{
    private const string WindowJson = """
    { "days": [1,2,3,4,5], "start": "22:00", "end": "05:00", "timezone": "America/Sao_Paulo" }
    """;

    [Test]
    public void InsideOvernightWindow()
    {
        // 2026-01-05 (segunda) 23:30 em São Paulo = 2026-01-06 02:30 UTC.
        var utc = new DateTime(2026, 1, 6, 2, 30, 0, DateTimeKind.Utc);
        Assert.That(PatchMaintenanceWindow.IsOpen(WindowJson, utc), Is.True);
    }

    [Test]
    public void OutsideWindow()
    {
        // 2026-01-05 12:00 em São Paulo = 15:00 UTC.
        var utc = new DateTime(2026, 1, 5, 15, 0, 0, DateTimeKind.Utc);
        Assert.That(PatchMaintenanceWindow.IsOpen(WindowJson, utc), Is.False);
    }

    [Test]
    public void InvalidJsonIsClosed()
    {
        Assert.That(PatchMaintenanceWindow.IsOpen("{not json", DateTime.UtcNow), Is.False);
    }

    [Test]
    public void ParsesDefaults()
    {
        var window = PatchMaintenanceWindow.Parse("""{ "start": "23:00" }""");
        Assert.That(window, Is.Not.Null);
        Assert.That(window!.Days, Has.Count.EqualTo(7));
        Assert.That(window.Start, Is.EqualTo(new TimeOnly(23, 0)));
        Assert.That(window.End, Is.EqualTo(new TimeOnly(5, 0)));
    }
}
