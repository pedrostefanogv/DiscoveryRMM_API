using Discovery.Core.Helpers;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class PatchDeadlineTests
{
    private static DateTime Utc(int y, int m, int d, int h = 0) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);

    [Test]
    public void DeadlineAnchorsOnPublishedAt()
    {
        var published = Utc(2026, 10, 1);
        Assert.That(PatchDeadline.DeadlineAt(7, published), Is.EqualTo(Utc(2026, 10, 8)));
        Assert.That(PatchDeadline.DeadlineAt(0, published), Is.Null, "0 = sem deadline");
        Assert.That(PatchDeadline.DeadlineAt(7, null), Is.Null, "sem data nao ha deadline");
    }

    [Test]
    public void PastDeadlineDetected()
    {
        var published = Utc(2026, 10, 1);
        Assert.That(PatchDeadline.IsPastDeadline(7, published, Utc(2026, 10, 7, 23)), Is.False);
        Assert.That(PatchDeadline.IsPastDeadline(7, published, Utc(2026, 10, 8)), Is.True);
        Assert.That(PatchDeadline.IsPastDeadline(7, null, Utc(2026, 10, 30)), Is.False,
            "sem data de publicacao nao inventa deadline");
    }

    [Test]
    public void WindowExtendsOnlyAfterDeadline()
    {
        var published = Utc(2026, 10, 1);

        var open = PatchDeadline.EvaluateWindow(false, 7, published, Utc(2026, 10, 8, 3));
        Assert.That(open.Open, Is.True, "D03: deadline vencido amplia a janela");
        Assert.That(open.Reason, Does.Contain("deadline"));

        var closed = PatchDeadline.EvaluateWindow(false, 7, published, Utc(2026, 10, 5));
        Assert.That(closed.Open, Is.False);

        var recurring = PatchDeadline.EvaluateWindow(true, 7, published, Utc(2026, 10, 5));
        Assert.That(recurring.Open, Is.True);
        Assert.That(recurring.Reason, Does.Contain("recorrente"));
    }
}
