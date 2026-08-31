using Discovery.Core.Helpers;
using NUnit.Framework;

namespace Discovery.Tests;

public class CronScheduleValidatorTests
{
    [TestCase("0 8 * * 1", true)]          // toda segunda 08:00
    [TestCase("*/5 * * * *", true)]        // a cada 5 min
    [TestCase("0 0 1 * *", true)]          // dia 1 de cada mês
    [TestCase("30 2 15 6 *", true)]        // 15/06 02:30
    [TestCase("0 9-17 * * mon-fri", true)] // nomes e intervalos
    [TestCase("0 0 * * sun", true)]
    [TestCase("0,15,30,45 * * * *", true)] // listas
    [TestCase("0 4 * * 0", true)]          // domingo (0)
    [TestCase("0 4 * * 7", true)]          // domingo (7)
    [TestCase("5 4 * * 1-5/2", true)]      // passo em intervalo
    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("   ", false)]
    [TestCase("* * * *", false)]           // 4 campos
    [TestCase("* * * * * *", false)]       // 6 campos (dialeto Quartz — não aceito)
    [TestCase("60 * * * *", false)]        // minuto fora do range
    [TestCase("* 24 * * *", false)]        // hora fora do range
    [TestCase("* * 0 * *", false)]         // dia do mês fora do range
    [TestCase("* * * 13 *", false)]        // mês fora do range
    [TestCase("* * * * 8", false)]         // dia da semana fora do range
    [TestCase("a b c d e", false)]         // não numérico
    [TestCase("5-2 * * * *", false)]       // range invertido
    [TestCase("*/0 * * * *", false)]       // passo zero
    public void IsValid_ReturnsExpected(string? cron, bool expected)
        => Assert.That(CronScheduleValidator.IsValid(cron), Is.EqualTo(expected));

    [Test]
    public void TryValidate_ReturnsErrorForInvalid()
    {
        var ok = CronScheduleValidator.TryValidate("0 8 * * 1", out var error);
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(error, Is.Null);
        });

        var ok2 = CronScheduleValidator.TryValidate("60 * * * *", out var error2);
        Assert.Multiple(() =>
        {
            Assert.That(ok2, Is.False);
            Assert.That(error2, Does.Contain("minute").IgnoreCase);
        });
    }
}
