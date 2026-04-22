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

        return createdAt.AddHours(profile.SlaHours);
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

        return createdAt.AddHours(profile.FirstResponseSlaHours);
    }

    /// <summary>
    /// Adiciona <paramref name="hours"/> horas úteis (conforme calendário) a <paramref name="from"/>.
    /// Pula fins de semana, feriados e horas fora do expediente.
    /// </summary>
    public static DateTime AddWorkingHours(DateTime from, int hours, SlaCalendar calendar)
    {
        if (hours <= 0) return from;

        var tz = TimeZoneInfo.FindSystemTimeZoneById(calendar.Timezone);
        var workDays = JsonSerializer.Deserialize<int[]>(calendar.WorkDaysJson) ?? [1, 2, 3, 4, 5];
        var holidayDates = calendar.Holidays.Select(h => h.Date.Date).ToHashSet();

        var current = TimeZoneInfo.ConvertTimeFromUtc(from.Kind == DateTimeKind.Utc ? from : DateTime.SpecifyKind(from, DateTimeKind.Utc), tz);
        var remaining = TimeSpan.FromHours(hours);

        while (remaining > TimeSpan.Zero)
        {
            // Se não é dia útil, avança para o início do próximo dia útil
            if (!IsWorkDay(current, workDays, holidayDates))
            {
                current = NextWorkDayStart(current, calendar, workDays, holidayDates);
                continue;
            }

            // Se antes do horário de expediente, avança para o início
            var dayStart = current.Date.AddHours(calendar.WorkDayStartHour);
            if (current < dayStart)
            {
                current = dayStart;
                continue;
            }

            // Se depois do horário de expediente, avança para o início do próximo dia útil
            var dayEnd = current.Date.AddHours(calendar.WorkDayEndHour);
            if (current >= dayEnd)
            {
                current = NextWorkDayStart(current, calendar, workDays, holidayDates);
                continue;
            }

            // Quantas horas restam hoje
            var todayRemaining = dayEnd - current;
            if (remaining <= todayRemaining)
            {
                current = current.Add(remaining);
                remaining = TimeSpan.Zero;
            }
            else
            {
                remaining -= todayRemaining;
                current = NextWorkDayStart(current, calendar, workDays, holidayDates);
            }
        }

        // Converter de volta para UTC
        return TimeZoneInfo.ConvertTimeToUtc(current, tz);
    }

    private static bool IsWorkDay(DateTime dt, int[] workDays, HashSet<DateTime> holidays)
        => workDays.Contains((int)dt.DayOfWeek) && !holidays.Contains(dt.Date);

    private static DateTime NextWorkDayStart(DateTime dt, SlaCalendar calendar, int[] workDays, HashSet<DateTime> holidays)
    {
        var next = dt.Date.AddDays(1).AddHours(calendar.WorkDayStartHour);
        while (!IsWorkDay(next, workDays, holidays))
            next = next.Date.AddDays(1).AddHours(calendar.WorkDayStartHour);
        return next;
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
            totalPausedSeconds += (int)(DateTime.UtcNow - ticket.SlaHoldStartedAt.Value).TotalSeconds;

        return ticket.SlaExpiresAt.Value.AddSeconds(totalPausedSeconds);
    }

    public async Task<(int HoursRemaining, double PercentUsed, bool Breached)> GetSlaStatusAsync(Guid ticketId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            throw new InvalidOperationException($"Ticket {ticketId} not found");

        if (!ticket.SlaExpiresAt.HasValue)
            return (0, 0, false);

        var effectiveExpiry = GetEffectiveSlaExpiry(ticket)!.Value;
        var now = DateTime.UtcNow;
        var totalSlaTime = (effectiveExpiry - ticket.CreatedAt).TotalHours;
        var elapsed = (now - ticket.CreatedAt).TotalHours;
        var remaining = (effectiveExpiry - now).TotalHours;

        var percentUsed = totalSlaTime > 0 ? Math.Min(100, (elapsed / totalSlaTime) * 100) : 0;
        var breached = now > effectiveExpiry;

        return (Math.Max(0, (int)remaining), percentUsed, breached);
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
        var totalFrtTime = (expiry - ticket.CreatedAt).TotalHours;
        var elapsed = (now - ticket.CreatedAt).TotalHours;
        var remaining = (expiry - now).TotalHours;

        var percentUsed = totalFrtTime > 0 ? Math.Min(100, (elapsed / totalFrtTime) * 100) : 0;
        var breached = now > expiry;

        return (Math.Max(0, (int)remaining), percentUsed, breached, false);
    }

    public async Task<bool> CheckAndLogSlaBreachAsync(Guid ticketId)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
        {
            _logger.LogWarning("Ticket {TicketId} not found during SLA check", ticketId);
            return false;
        }

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
                ticketId,
                TicketActivityType.SlaBreached,
                null,
                effectiveExpiry.ToString("o"),
                now.ToString("o"),
                "SLA violation detected"
            );

            _logger.LogWarning("SLA Breached for ticket {TicketId} (effective expiry {ExpiresAt})",
                ticketId, effectiveExpiry);

            return true;
        }

        return false;
    }
}

