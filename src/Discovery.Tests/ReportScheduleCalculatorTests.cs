using Discovery.Core.Helpers;

namespace Discovery.Tests;

/// <summary>
/// O agendamento da UI (frequencia/dia/hora) vira cron do Quartz e precisa
/// calcular a proxima ocorrencia em UTC — a base do NextTriggerAt.
/// </summary>
public class ReportScheduleCalculatorTests
{
    [Test]
    public void BuildCron_Weekly_UsesDayOfWeekField()
    {
        var cron = ReportScheduleCalculator.BuildCron(ReportScheduleCalculator.Weekly, dayOfWeek: 2, dayOfMonth: null, hourUtc: 8, minuteUtc: 30);
        Assert.That(cron, Is.EqualTo("0 30 8 ? * 2"));
    }

    [Test]
    public void BuildCron_Monthly_UsesDayOfMonthField()
    {
        var cron = ReportScheduleCalculator.BuildCron(ReportScheduleCalculator.Monthly, dayOfWeek: null, dayOfMonth: 15, hourUtc: 6, minuteUtc: 5);
        Assert.That(cron, Is.EqualTo("0 5 6 15 * ?"));
    }

    [Test]
    public void ParseCron_RoundTrips()
    {
        var cron = ReportScheduleCalculator.BuildCron(ReportScheduleCalculator.Weekly, 3, null, 21, 45);
        var parsed = ReportScheduleCalculator.ParseCron(cron);

        Assert.That(parsed.Frequency, Is.EqualTo(ReportScheduleCalculator.Weekly));
        Assert.That(parsed.DayOfWeek, Is.EqualTo(3));
        Assert.That(parsed.HourUtc, Is.EqualTo(21));
        Assert.That(parsed.MinuteUtc, Is.EqualTo(45));
    }

    [Test]
    public void ComputeNext_Daily_RollsToNextDayWhenTimePassed()
    {
        var from = new DateTime(2026, 10, 4, 18, 0, 0, DateTimeKind.Utc);
        var next = ReportScheduleCalculator.ComputeNextOccurrenceUtc(ReportScheduleCalculator.Daily, null, null, 8, 0, from);

        Assert.That(next, Is.EqualTo(new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void ComputeNext_Monthly_ClampsToShorterMonth()
    {
        // Dia 31 em fevereiro deve cair no ultimo dia do mes.
        var from = new DateTime(2026, 1, 31, 9, 0, 0, DateTimeKind.Utc);
        var next = ReportScheduleCalculator.ComputeNextOccurrenceUtc(ReportScheduleCalculator.Monthly, null, 31, 8, 0, from);

        Assert.That(next, Is.EqualTo(new DateTime(2026, 2, 28, 8, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void ComputeNext_Weekly_FindsNextMatchingDay()
    {
        // 2026-10-04 e domingo; proxima segunda (quartz 2) as 07:00.
        var from = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var next = ReportScheduleCalculator.ComputeNextOccurrenceUtc(ReportScheduleCalculator.Weekly, 2, null, 7, 0, from);

        Assert.That(next, Is.EqualTo(new DateTime(2026, 10, 5, 7, 0, 0, DateTimeKind.Utc)));
    }
}
