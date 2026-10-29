using Discovery.Core.Helpers;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class PatchMaintenanceWindowProjectionTests
{
    private const string DailyWindow =
        """{"days":[1,2,3,4,5,6,7],"start":"22:00","end":"05:00"}""";

    private static DateTime Utc(int y, int m, int d, int h, int min = 0) =>
        new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Test]
    public void ProjectsNextStartWhenClosed()
    {
        var occurrence = PatchMaintenanceWindow.NextOccurrence(DailyWindow, Utc(2026, 10, 29, 12));

        Assert.That(occurrence, Is.Not.Null);
        Assert.That(occurrence!.StartUtc, Is.EqualTo(Utc(2026, 10, 29, 22)));
        Assert.That(occurrence.EndUtc, Is.EqualTo(Utc(2026, 10, 30, 5)));
        Assert.That(occurrence.Duration, Is.EqualTo(TimeSpan.FromHours(7)));
        Assert.That(occurrence.Id, Does.StartWith("win-"));
    }

    [Test]
    public void ReturnsCurrentOccurrenceWhenOpen()
    {
        var occurrence = PatchMaintenanceWindow.NextOccurrence(DailyWindow, Utc(2026, 10, 29, 23));

        Assert.That(occurrence!.StartUtc, Is.EqualTo(Utc(2026, 10, 29, 22)));
        Assert.That(occurrence.EndUtc, Is.EqualTo(Utc(2026, 10, 30, 5)));
    }

    [Test]
    public void EarlyMorningBelongsToPreviousDayWindow()
    {
        var occurrence = PatchMaintenanceWindow.NextOccurrence(DailyWindow, Utc(2026, 10, 30, 2));

        Assert.That(occurrence!.StartUtc, Is.EqualTo(Utc(2026, 10, 29, 22)),
            "madrugada pertence a janela que abriu no dia anterior");
        Assert.That(occurrence.EndUtc, Is.EqualTo(Utc(2026, 10, 30, 5)));
    }

    [Test]
    public void RespectsSelectedDays()
    {
        // 2026-10-29 é quinta-feira (ISO 4); só segunda (1) abre.
        var json = """{"days":[1],"start":"22:00","end":"23:00"}""";
        var occurrence = PatchMaintenanceWindow.NextOccurrence(json, Utc(2026, 10, 29, 12));

        Assert.That(occurrence, Is.Not.Null);
        Assert.That(occurrence!.StartUtc.DayOfWeek, Is.EqualTo(DayOfWeek.Monday));
    }

    [Test]
    public void AppliesWindowTimezone()
    {
        var json = """{"days":[1,2,3,4,5,6,7],"start":"22:00","end":"23:00","timezone":"America/Sao_Paulo"}""";
        var occurrence = PatchMaintenanceWindow.NextOccurrence(json, Utc(2026, 10, 29, 12));

        // 22:00 BRT (UTC-3) = 01:00 UTC do dia seguinte.
        Assert.That(occurrence!.StartUtc, Is.EqualTo(Utc(2026, 10, 30, 1)));
    }

    [Test]
    public void DstGapStillOpensAtFirstValidInstant()
    {
        // 2027-03-14: Nova York salta de 02:00 para 03:00 (02:30 não existe).
        var json = """{"days":[1,2,3,4,5,6,7],"start":"02:30","end":"04:00","timezone":"America/New_York"}""";
        var occurrence = PatchMaintenanceWindow.NextOccurrence(json, Utc(2027, 3, 13, 12));

        Assert.That(occurrence, Is.Not.Null);
        // 03:00 EDT (UTC-4) = 07:00 UTC.
        Assert.That(occurrence!.StartUtc, Is.EqualTo(Utc(2027, 3, 14, 7)));
    }

    [Test]
    public void WindowIdIsStableForSameOccurrence()
    {
        var first = PatchMaintenanceWindow.NextOccurrence(DailyWindow, Utc(2026, 10, 29, 12));
        var second = PatchMaintenanceWindow.NextOccurrence(DailyWindow, Utc(2026, 10, 29, 12, 30));

        Assert.That(first!.Id, Is.EqualTo(second!.Id),
            "UI e job precisam concordar no identificador da ocorrência");
        Assert.That(PatchMaintenanceWindow.BuildWindowId(first.StartUtc), Is.EqualTo(first.Id));
    }

    [Test]
    public void ReturnsNullWithoutWindow()
    {
        Assert.That(PatchMaintenanceWindow.NextOccurrence(null, Utc(2026, 10, 29, 12)), Is.Null);
        Assert.That(PatchMaintenanceWindow.NextOccurrence("   ", Utc(2026, 10, 29, 12)), Is.Null);
        Assert.That(PatchMaintenanceWindow.NextOccurrence("nao-e-json", Utc(2026, 10, 29, 12)), Is.Null);
        // "{}" usa os defaults documentados do parser (todos os dias, 22:00–05:00).
        Assert.That(PatchMaintenanceWindow.NextOccurrence("{}", Utc(2026, 10, 29, 12)), Is.Not.Null);
    }
}
