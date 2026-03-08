using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class NotificationRepository : INotificationRepository
{
    private readonly MeduzaDbContext _db;

    public NotificationRepository(MeduzaDbContext db) => _db = db;

    public async Task<AppNotification> CreateAsync(AppNotification notification)
    {
        notification.Id = IdGenerator.NewId();
        notification.CreatedAt = DateTime.UtcNow;

        _db.AppNotifications.Add(notification);
        await _db.SaveChangesAsync();
        return notification;
    }

    public async Task<IReadOnlyList<AppNotification>> GetRecentAsync(Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null, string? topic = null, NotificationSeverity? severity = null, bool? isRead = null, int limit = 50)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);

        var query = _db.AppNotifications.AsNoTracking().AsQueryable();

        if (recipientUserId.HasValue)
            query = query.Where(notification => notification.RecipientUserId == recipientUserId.Value);

        if (recipientAgentId.HasValue)
            query = query.Where(notification => notification.RecipientAgentId == recipientAgentId.Value);

        if (!string.IsNullOrWhiteSpace(recipientKey))
            query = query.Where(notification => notification.RecipientKey == recipientKey);

        if (!string.IsNullOrWhiteSpace(topic))
            query = query.Where(notification => notification.Topic == topic);

        if (severity.HasValue)
            query = query.Where(notification => notification.Severity == severity.Value);

        if (isRead.HasValue)
            query = query.Where(notification => notification.IsRead == isRead.Value);

        return await query
            .OrderByDescending(notification => notification.CreatedAt)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task<bool> MarkAsReadAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null)
    {
        var query = _db.AppNotifications.Where(notification => notification.Id == id);

        if (recipientUserId.HasValue)
            query = query.Where(notification => notification.RecipientUserId == recipientUserId.Value);

        if (recipientAgentId.HasValue)
            query = query.Where(notification => notification.RecipientAgentId == recipientAgentId.Value);

        if (!string.IsNullOrWhiteSpace(recipientKey))
            query = query.Where(notification => notification.RecipientKey == recipientKey);

        var notification = await query.FirstOrDefaultAsync();
        if (notification is null)
            return false;

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return true;
    }
}
