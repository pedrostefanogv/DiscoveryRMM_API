using Discovery.Core.Helpers;

namespace Discovery.Tests;

/// <summary>
/// Regressao do bug B1: "30 dias" era clampado para 168h, fazendo a janela de
/// 30 dias compartilhar a MESMA chave de cache do dashboard de 7 dias.
/// </summary>
public class DashboardWindowTests
{
    [Test]
    public void NormalizeWindowHours_MapsAllDashboardWindows()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DashboardCacheKeys.NormalizeWindowHours(TimeSpan.FromHours(24)), Is.EqualTo(24));
            Assert.That(DashboardCacheKeys.NormalizeWindowHours(TimeSpan.FromDays(7)), Is.EqualTo(168));
            Assert.That(DashboardCacheKeys.NormalizeWindowHours(TimeSpan.FromDays(30)), Is.EqualTo(720));
        });
    }

    [Test]
    public void NormalizeWindowHours_ClampsToSupportedRange()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DashboardCacheKeys.NormalizeWindowHours(TimeSpan.FromMinutes(1)), Is.EqualTo(1));
            Assert.That(DashboardCacheKeys.NormalizeWindowHours(TimeSpan.FromDays(365)), Is.EqualTo(24 * 30));
        });
    }

    [Test]
    public void GlobalSummary_SevenDaysAndThirtyDays_UseDistinctCacheKeys()
    {
        var sevenDays = DashboardCacheKeys.GlobalSummary(
            DashboardCacheKeys.NormalizeWindowHours(TimeSpan.FromDays(7)));
        var thirtyDays = DashboardCacheKeys.GlobalSummary(
            DashboardCacheKeys.NormalizeWindowHours(TimeSpan.FromDays(30)));

        Assert.Multiple(() =>
        {
            Assert.That(sevenDays, Is.Not.EqualTo(thirtyDays));
            Assert.That(sevenDays, Does.Contain("168h"));
            Assert.That(thirtyDays, Does.Contain("720h"));
            Assert.That(DashboardCacheKeys.SupportedWindowHours, Does.Contain(720));
        });
    }
}
