using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Discovery.Infrastructure.Services;

public class SlaService : ISlaService
{
    private readonly IWorkflowProfileRepository _workflowProfileRepo;
    private readonly ITicketRepository _ticketRepo;
    private readonly IActivityLogService _activityLogService;
    private readonly ISlaCalendarRepository _calendarRepo;
    private readonly ILogger<SlaService> _logger;

    /// <summary>Limiar de aviso usado quando o perfil não define um.</summary>
    public const int DefaultSlaWarningThresholdPercent = 80;

    public SlaService(
        IWorkflowProfileRepository workflowProfileRepo,
        ITicketRepository ticketRepo,
        IActivityLogService activityLogService,
        ISlaCalendarRepository calendarRepo,
        ILogger<SlaService> logger)
    {
        _workflowProfileRepo = workflowProfileRepo;
        _ticketRepo = ticketRepo;
        _activityLogService = activityLogService;
        _calendarRepo = calendarRepo;
        _logger = logger;
    }

    public async Task<DateTime> CalculateSlaExpiryAsync(Guid workflowProfileId, DateTime createdAt)
    {
        var profile = await _workflowProfileRepo.GetByIdAsync(workflowProfileId);
        if (profile is null)
            throw new InvalidOperationException($"WorkflowProfile {workflowProfileId} not found");

        if (profile.SlaCalendarId.HasValue)
        {
            var calendar = await _calendarRepo.GetByIdAsync(profile.SlaCalendarId.Value);
            if (calendar is not null)
                return AddWorkingHours(createdAt, profile.SlaHours, calendar);
        }

        return DateTime.SpecifyKind(createdAt.AddHours(profile.SlaHours), DateTimeKind.Utc);
    }

    public async Task<DateTime> CalculateFirstResponseExpiryAsync(Guid workflowProfileId, DateTime createdAt)
    {
        var profile = await _workflowProfileRepo.GetByIdAsync(workflowProfileId);
        if (profile is null)
            throw new InvalidOperationException($"WorkflowProfile {workflowProfileId} not found");

        if (profile.SlaCalendarId.HasValue)
        {
            var calendar = await _calendarRepo.GetByIdAsync(profile.SlaCalendarId.Value);
            if (calendar is not null)
                return AddWorkingHours(createdAt, profile.FirstResponseSlaHours, calendar);
        }

        return DateTime.SpecifyKind(createdAt.AddHours(profile.FirstResponseSlaHours), DateTimeKind.Utc);
    }

    private async Task<SlaCalendar?> ResolveCalendarAsync(Ticket ticket)
    {
        if (!ticket.WorkflowProfileId.HasValue) return null;

        var profile = await _workflowProfileRepo.GetByIdAsync(ticket.WorkflowProfileId.Value);
        if (profile?.SlaCalendarId is null) return null;

        return await _calendarRepo.GetByIdAsync(profile.SlaCalendarId.Value);
    }

    /// <summary>
    /// Resolve, em uma única leitura do perfil, o calendário e o limiar de aviso.
    /// O job de monitoramento chama isto uma vez por perfil e reaproveita o
    /// resultado para todos os tickets daquele perfil.
    /// </summary>
    public async Task<TicketSlaContext> GetSlaContextForTicketAsync(Ticket ticket)
    {
        if (!ticket.WorkflowProfileId.HasValue)
            return new TicketSlaContext(null, DefaultSlaWarningThresholdPercent);

        var profile = await _workflowProfileRepo.GetByIdAsync(ticket.WorkflowProfileId.Value);
        if (profile is null)
            return new TicketSlaContext(null, DefaultSlaWarningThresholdPercent);

        var threshold = profile.SlaWarningPercent is > 0 and <= 100
            ? profile.SlaWarningPercent.Value
            : DefaultSlaWarningThresholdPercent;

        if (profile.SlaCalendarId is null)
            return new TicketSlaContext(null, threshold);

        var calendar = await _calendarRepo.GetByIdAsync(profile.SlaCalendarId.Value);
        return new TicketSlaContext(calendar, threshold);
    }

    // ── Resolução defensiva de configuração ──────────────────────────────
    // Um calendário mal configurado (fuso inexistente ou lista de dias vazia)
    // não pode derrubar a criação de chamados nem travar o job de SLA.

    /// <summary>Resolve o fuso; usa UTC quando o identificador é inválido.</summary>
    internal static TimeZoneInfo ResolveTimeZone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone)) return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Lê os dias úteis do JSON; normaliza valores inválidos e cai para Seg-Sex
    /// quando a lista é vazia/inválida (evita laço infinito no avanço de dias).
    /// </summary>
    internal static int[] ResolveWorkDays(string? workDaysJson)
    {
        if (!string.IsNullOrWhiteSpace(workDaysJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<int[]>(workDaysJson);
                var valid = parsed?
                    .Where(day => day >= 0 && day <= 6)
                    .Distinct()
                    .OrderBy(day => day)
                    .ToArray();

                if (valid is { Length: > 0 }) return valid;
            }
            catch (JsonException)
            {
                // JSON inválido: usa o padrão abaixo.
            }
        }

        return [1, 2, 3, 4, 5];
    }

    /// <summary>
    /// Converte um horário local para UTC. Horários inexistentes (salto do horário
    /// de verão) não devem quebrar o cálculo: cai para o offset do instante.
    /// </summary>
    private static DateTime LocalToUtcSafe(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
        }
        catch (ArgumentException)
        {
            var offset = tz.GetUtcOffset(unspecified);
            return DateTime.SpecifyKind(unspecified - offset, DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// Janela de um turno que COMEÇA no dia local <paramref name="shiftDay"/>.
    /// Quando o fim é menor que o início (ex.: 22h→6h), o turno vira o dia.
    /// </summary>
    private static (DateTime Start, DateTime End) ShiftWindow(DateTime shiftDay, SlaCalendar calendar)
    {
        var start = shiftDay.Date.AddHours(calendar.WorkDayStartHour);
        var end = shiftDay.Date.AddHours(calendar.WorkDayEndHour);

        if (calendar.WorkDayEndHour < calendar.WorkDayStartHour)
            end = end.AddDays(1); // turno noturno

        return (start, end);
    }

    /// <summary>Um dia é "dia de turno" quando é dia útil e não é feriado.</summary>
    private static bool IsShiftDay(DateTime shiftDay, int[] workDays, HashSet<DateTime> holidays)
        => workDays.Contains((int)shiftDay.DayOfWeek) && !holidays.Contains(shiftDay.Date);

    /// <summary>
    /// Adiciona <paramref name="hours"/> horas úteis (conforme calendário) a <paramref name="from"/>.
    /// Pula fins de semana, feriados e horas fora do expediente. Suporta turnos
    /// que viram o dia (fim menor que início) e durações reais em transições de
    /// horário de verão.
    /// </summary>
    public static DateTime AddWorkingHours(DateTime from, int hours, SlaCalendar calendar)
    {
        var fromUtc = from.Kind == DateTimeKind.Utc ? from : DateTime.SpecifyKind(from, DateTimeKind.Utc);

        if (hours <= 0) return fromUtc;

        var tz = ResolveTimeZone(calendar.Timezone);
        var workDays = ResolveWorkDays(calendar.WorkDaysJson);

        // A janela de feriados precisa cobrir todo o avanço do laço.
        var maxDaysForward = Math.Clamp(hours * 8 + 90, 366, 3660);
        var holidayDates = GetEffectiveHolidayDates(calendar, fromUtc, maxDaysForward);

        var current = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, tz);
        var remaining = TimeSpan.FromHours(hours);

        // Começa um dia antes para capturar um turno noturno iniciado ontem.
        var shiftDay = current.Date.AddDays(-1);

        // Limite defensivo (~10 anos de turnos) contra dado corrompido.
        for (var guard = 0; remaining > TimeSpan.Zero && guard < 4000; guard++)
        {
            if (!IsShiftDay(shiftDay, workDays, holidayDates))
            {
                shiftDay = shiftDay.AddDays(1);
                continue;
            }

            var (start, end) = ShiftWindow(shiftDay, calendar);

            if (current >= end)
            {
                shiftDay = shiftDay.AddDays(1);
                continue;
            }

            if (current < start)
                current = start;

            // Duração real do que sobra no turno, considerando horário de verão.
            var currentUtc = LocalToUtcSafe(current, tz);
            var endUtc = LocalToUtcSafe(end, tz);
            var available = endUtc - currentUtc;

            if (remaining <= available)
            {
                current = TimeZoneInfo.ConvertTimeFromUtc(currentUtc + remaining, tz);
                remaining = TimeSpan.Zero;
            }
            else
            {
                remaining -= available;
                current = end;
                shiftDay = shiftDay.AddDays(1);
            }
        }

        return LocalToUtcSafe(current, tz);
    }

    /// <summary>
    /// Conta as horas úteis entre dois instantes UTC conforme o calendário.
    /// Usa os mesmos turnos de <see cref="AddWorkingHours"/> (inclusive noturnos)
    /// e mede a duração real em UTC, correta em transições de horário de verão.
    /// </summary>
    public static double CountWorkingHours(DateTime from, DateTime to, SlaCalendar calendar)
    {
        var fromUtc = from.Kind == DateTimeKind.Utc ? from : DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toUtc = to.Kind == DateTimeKind.Utc ? to : DateTime.SpecifyKind(to, DateTimeKind.Utc);
        if (toUtc <= fromUtc) return 0;

        var tz = ResolveTimeZone(calendar.Timezone);
        var workDays = ResolveWorkDays(calendar.WorkDaysJson);
        var startLocal = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, tz);
        var endLocal = TimeZoneInfo.ConvertTimeFromUtc(toUtc, tz);

        var spanDays = (int)Math.Ceiling((endLocal.Date - startLocal.Date).TotalDays) + 2;
        var holidays = GetEffectiveHolidayDates(calendar, fromUtc, Math.Clamp(spanDays, 1, 3660));

        double total = 0;

        // Um dia antes para capturar turno noturno iniciado antes da janela.
        for (var shiftDay = startLocal.Date.AddDays(-1); shiftDay <= endLocal.Date; shiftDay = shiftDay.AddDays(1))
        {
            if (!IsShiftDay(shiftDay, workDays, holidays)) continue;

            var (start, end) = ShiftWindow(shiftDay, calendar);
            var windowStartUtc = LocalToUtcSafe(start, tz);
            var windowEndUtc = LocalToUtcSafe(end, tz);

            var overlapStart = windowStartUtc > fromUtc ? windowStartUtc : fromUtc;
            var overlapEnd = windowEndUtc < toUtc ? windowEndUtc : toUtc;

            if (overlapEnd > overlapStart)
                total += (overlapEnd - overlapStart).TotalHours;
        }

        return total;
    }

    /// <summary>
    /// Calcula todas as datas de feriado efetivas para o período, expandindo
    /// feriados Yearly (recorrentes) e Relative (calculados por regra).
    /// Primeiro resolve fixos/anuais e depois os relativos, para que o cálculo
    /// de "enésimo dia útil" já considere os feriados fixos/anuais.
    /// </summary>
    public static HashSet<DateTime> GetEffectiveHolidayDates(SlaCalendar calendar, DateTime fromUtc, int maxDaysForward)
    {
        var result = new HashSet<DateTime>();
        var fromTz = fromUtc.Kind == DateTimeKind.Utc ? fromUtc : DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        var tz = ResolveTimeZone(calendar.Timezone);
        var start = TimeZoneInfo.ConvertTimeFromUtc(fromTz, tz).Date;
        var end = start.AddDays(Math.Max(0, maxDaysForward));
        var workDays = ResolveWorkDays(calendar.WorkDaysJson);

        // Passo 1: feriados de data fixa e recorrentes anuais.
        foreach (var holiday in calendar.Holidays)
        {
            var type = (HolidayType)holiday.HolidayTypeValue;

            switch (type)
            {
                case HolidayType.Fixed:
                    if (holiday.Date is { } fixedDate && fixedDate.Date >= start && fixedDate.Date <= end)
                        result.Add(fixedDate.Date);
                    break;

                case HolidayType.Yearly:
                    if (holiday.Date is not { } yearlySource) break;
                    for (var y = start.Year; y <= end.Year; y++)
                    {
                        try
                        {
                            var yearlyDate = new DateTime(y, yearlySource.Month, yearlySource.Day);
                            if (yearlyDate >= start && yearlyDate <= end)
                                result.Add(yearlyDate);
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            // Ignora datas inválidas (ex: 29/fev em ano não bissexto)
                        }
                    }
                    break;
            }
        }

        // Passo 2: feriados relativos, usando os dias úteis do calendário e os
        // feriados já resolvidos no passo 1.
        foreach (var holiday in calendar.Holidays)
        {
            if ((HolidayType)holiday.HolidayTypeValue != HolidayType.Relative) continue;

            foreach (var date in CalculateRelativeHolidayDates(holiday, start, end, workDays, result))
                result.Add(date);
        }

        return result;
    }

    /// <summary>
    /// Calcula as datas de um feriado relativo dentro do intervalo [start, end].
    /// </summary>
    public static List<DateTime> CalculateRelativeHolidayDates(
        SlaCalendarHoliday holiday, DateTime start, DateTime end,
        int[]? workDays = null, HashSet<DateTime>? knownHolidays = null)
    {
        var dates = new List<DateTime>();
        var method = holiday.RelativeMethod ?? RelativeHolidayMethod.DayOfWeekOccurrence;
        var month = holiday.RelativeMonth ?? 1;
        var occurrence = holiday.RelativeOccurrence ?? 1;
        var effectiveWorkDays = workDays is { Length: > 0 } ? workDays : [1, 2, 3, 4, 5];
        var holidayDates = knownHolidays ?? [];

        for (var y = start.Year; y <= end.Year; y++)
        {
            DateTime calculated;

            if (method == RelativeHolidayMethod.NthBusinessDay)
            {
                // Enésimo dia útil do mês, considerando dias úteis e feriados do calendário.
                calculated = GetNthBusinessDayOfMonth(y, month, occurrence, effectiveWorkDays, holidayDates);
            }
            else
            {
                // Enésimo dia da semana do mês
                var dayOfWeek = holiday.RelativeDayOfWeek ?? 1; // default: segunda
                calculated = GetNthDayOfWeekOfMonth(y, month, dayOfWeek, occurrence);
            }

            if (calculated >= start && calculated <= end)
                dates.Add(calculated);
        }

        return dates;
    }

    private static DateTime GetNthDayOfWeekOfMonth(int year, int month, int dayOfWeek, int occurrence)
    {
        // occurrence 1..5 (5 = última)
        var firstDay = new DateTime(year, month, 1);
        var daysInMonth = DateTime.DaysInMonth(year, month);

        if (occurrence <= 4)
        {
            var diff = (dayOfWeek - (int)firstDay.DayOfWeek + 7) % 7;
            var day = 1 + diff + (occurrence - 1) * 7;
            if (day > daysInMonth)
                day = daysInMonth; // cai para o último dia do mês se extrapolar
            return new DateTime(year, month, day);
        }
        else
        {
            // occurrence = 5 ou mais = último
            var lastDay = new DateTime(year, month, daysInMonth);
            var diff = ((int)lastDay.DayOfWeek - dayOfWeek + 7) % 7;
            return lastDay.AddDays(-diff);
        }
    }

    private static DateTime GetNthBusinessDayOfMonth(int year, int month, int occurrence, int[] workDays, HashSet<DateTime> holidayDates)
    {
        var businessDays = 0;
        var daysInMonth = DateTime.DaysInMonth(year, month);

        for (var day = 1; day <= daysInMonth; day++)
        {
            var dt = new DateTime(year, month, day);
            if (workDays.Contains((int)dt.DayOfWeek) && !holidayDates.Contains(dt.Date))
                businessDays++;

            if (businessDays == occurrence)
                return dt;
        }

        // Se não achou a ocorrência, retorna o último dia útil do mês
        for (var day = daysInMonth; day >= 1; day--)
        {
            var dt = new DateTime(year, month, day);
            if (workDays.Contains((int)dt.DayOfWeek) && !holidayDates.Contains(dt.Date))
                return dt;
        }

        return new DateTime(year, month, 1);
    }

    /// <summary>
    /// Retorna a expiração efetiva do SLA, adicionando o tempo pausado acumulado.
    /// </summary>
    public DateTime? GetEffectiveSlaExpiry(Ticket ticket)
    {
        if (!ticket.SlaExpiresAt.HasValue) return null;

        var totalPausedSeconds = ticket.SlaPausedSeconds;

        // Se ainda está em pausa agora, somar o tempo corrente
        if (ticket.SlaHoldStartedAt.HasValue)
        {
            var heldFor = (DateTime.UtcNow - ticket.SlaHoldStartedAt.Value).TotalSeconds;
            if (heldFor > 0) totalPausedSeconds += (int)heldFor;
        }

        // Garante que o retorno seja UTC (o banco agora armazena timestamptz)
        var expiry = ticket.SlaExpiresAt.Value.AddSeconds(totalPausedSeconds);
        return expiry.Kind == DateTimeKind.Utc ? expiry : DateTime.SpecifyKind(expiry, DateTimeKind.Utc);
    }

    public async Task<(int HoursRemaining, double PercentUsed, bool Breached)> GetSlaStatusAsync(Guid ticketId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            throw new InvalidOperationException($"Ticket {ticketId} not found");

        var calendar = await ResolveCalendarAsync(ticket);
        return GetSlaStatus(ticket, calendar);
    }

    /// <summary>
    /// Status do SLA de resolução sem I/O, para quando o ticket já foi carregado.
    /// </summary>
    public (int HoursRemaining, double PercentUsed, bool Breached) GetSlaStatus(Ticket ticket, SlaCalendar? calendar)
    {
        if (!ticket.SlaExpiresAt.HasValue)
            return (0, 0, false);

        var effectiveExpiry = GetEffectiveSlaExpiry(ticket)!.Value;
        var now = DateTime.UtcNow;

        double totalSlaTime, elapsed, remainingHours;
        if (calendar is not null)
        {
            // SLA em horas úteis: medir tudo em horas úteis (antes o gasto era
            // relógio de parede e estourava o percentual fora do expediente).
            totalSlaTime = CountWorkingHours(ticket.CreatedAt, effectiveExpiry, calendar);
            elapsed = CountWorkingHours(ticket.CreatedAt, now, calendar);
            remainingHours = CountWorkingHours(now, effectiveExpiry, calendar);
        }
        else
        {
            totalSlaTime = (effectiveExpiry - ticket.CreatedAt).TotalHours;
            elapsed = (now - ticket.CreatedAt).TotalHours;
            remainingHours = (effectiveExpiry - now).TotalHours;
        }

        var percentUsed = totalSlaTime > 0 ? Math.Min(100, (elapsed / totalSlaTime) * 100) : 0;
        var breached = now > effectiveExpiry;

        return (Math.Max(0, (int)Math.Ceiling(remainingHours)), percentUsed, breached);
    }

    public async Task<(int HoursRemaining, double PercentUsed, bool Breached, bool Achieved)> GetFrtStatusAsync(Guid ticketId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            throw new InvalidOperationException($"Ticket {ticketId} not found");

        if (!ticket.SlaFirstResponseExpiresAt.HasValue)
            return (0, 0, false, false);

        // FRT já foi alcançado
        if (ticket.FirstRespondedAt.HasValue)
        {
            var achieved = ticket.FirstRespondedAt.Value <= ticket.SlaFirstResponseExpiresAt.Value;
            return (0, 100, !achieved, achieved);
        }

        var expiry = ticket.SlaFirstResponseExpiresAt.Value;
        var now = DateTime.UtcNow;
        var calendar = await ResolveCalendarAsync(ticket);

        // Base do FRT: início explícito (reiniciado na reabertura) ou a criação.
        var frtBase = ticket.FirstResponseSlaStartedAt ?? ticket.CreatedAt;

        double totalFrtTime, elapsed, remainingHours;
        if (calendar is not null)
        {
            totalFrtTime = CountWorkingHours(frtBase, expiry, calendar);
            elapsed = CountWorkingHours(frtBase, now, calendar);
            remainingHours = CountWorkingHours(now, expiry, calendar);
        }
        else
        {
            totalFrtTime = (expiry - frtBase).TotalHours;
            elapsed = (now - frtBase).TotalHours;
            remainingHours = (expiry - now).TotalHours;
        }

        var percentUsed = totalFrtTime > 0 ? Math.Min(100, (elapsed / totalFrtTime) * 100) : 0;
        var breached = now > expiry;

        return (
            Math.Max(0, (int)Math.Ceiling(remainingHours)),
            percentUsed,
            breached,
            false
        );
    }

    public async Task<bool> CheckAndLogSlaBreachAsync(Guid ticketId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
        {
            _logger.LogWarning("Ticket {TicketId} not found during SLA check", ticketId);
            return false;
        }

        return await CheckAndLogSlaBreachAsync(ticket);
    }

    /// <summary>
    /// Verifica e registra violação reaproveitando o ticket já carregado
    /// (evita recarregar no job de monitoramento).
    /// </summary>
    public async Task<bool> CheckAndLogSlaBreachAsync(Ticket ticket)
    {
        if (ticket.SlaBreached)
            return false; // Já foi marcado como violado

        if (!ticket.SlaExpiresAt.HasValue)
            return false; // Sem SLA

        var effectiveExpiry = GetEffectiveSlaExpiry(ticket)!.Value;
        var now = DateTime.UtcNow;

        if (now > effectiveExpiry)
        {
            ticket.SlaBreached = true;
            await _ticketRepo.UpdateAsync(ticket);

            await _activityLogService.LogActivityAsync(
                ticket.Id,
                TicketActivityType.SlaBreached,
                null,
                effectiveExpiry.ToString("o"),
                now.ToString("o"),
                "SLA violation detected"
            );

            _logger.LogWarning("SLA Breached for ticket {TicketId} (effective expiry {ExpiresAt})",
                ticket.Id, effectiveExpiry);

            return true;
        }

        return false;
    }
}
