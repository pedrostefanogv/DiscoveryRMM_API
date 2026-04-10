using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Discovery.Infrastructure.Repositories;

public class ReportTemplateRepository : IReportTemplateRepository
{
    private readonly DiscoveryDbContext _db;

    public ReportTemplateRepository(DiscoveryDbContext db) => _db = db;

    public async Task<ReportTemplate> CreateAsync(ReportTemplate template)
    {
        template.Id = IdGenerator.NewId();
        template.CreatedAt = DateTime.UtcNow;
        template.UpdatedAt = template.CreatedAt;

        _db.ReportTemplates.Add(template);
        _db.ReportTemplateHistories.Add(BuildHistorySnapshot(template, "Created", template.Version));
        await _db.SaveChangesAsync();
        return template;
    }

    public async Task<ReportTemplate?> GetByIdAsync(Guid id, Guid? clientId = null)
    {
        return await _db.ReportTemplates
            .AsNoTracking()
            .Where(template => template.Id == id)
            .Where(template => clientId == null || template.ClientId == null || template.ClientId == clientId)
            .FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<ReportTemplate>> GetAllAsync(Guid? clientId = null, ReportDatasetType? datasetType = null, bool? isActive = true)
    {
        IQueryable<ReportTemplate> query = _db.ReportTemplates.AsNoTracking();

        if (clientId.HasValue)
        {
            query = query.Where(template => template.ClientId == null || template.ClientId == clientId.Value);
        }

        if (datasetType.HasValue)
        {
            query = query.Where(template => template.DatasetType == datasetType.Value);
        }

        if (isActive.HasValue)
        {
            query = query.Where(template => template.IsActive == isActive.Value);
        }

        return await query
            .OrderBy(template => template.Name)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<ReportTemplateHistory>> GetHistoryAsync(Guid templateId, int limit = 50)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);

        return await _db.ReportTemplateHistories
            .AsNoTracking()
            .Where(history => history.TemplateId == templateId)
            .OrderByDescending(history => history.Version)
            .Take(safeLimit)
            .ToListAsync();
    }

    public async Task UpdateAsync(ReportTemplate template)
    {
        var current = await _db.ReportTemplates.FirstOrDefaultAsync(item => item.Id == template.Id);
        if (current is null)
            throw new InvalidOperationException($"Report template {template.Id} not found.");

        current.Name = template.Name;
        current.Description = template.Description;
        current.Instructions = template.Instructions;
        current.ExecutionSchemaJson = template.ExecutionSchemaJson;
        current.DatasetType = template.DatasetType;
        current.DefaultFormat = template.DefaultFormat;
        current.LayoutJson = template.LayoutJson;
        current.FiltersJson = template.FiltersJson;
        current.IsActive = template.IsActive;
        current.Version += 1;
        current.UpdatedAt = DateTime.UtcNow;
        current.UpdatedBy = template.UpdatedBy;

        _db.ReportTemplateHistories.Add(BuildHistorySnapshot(current, "Updated", current.Version));

        await _db.SaveChangesAsync();
    }

    public async Task<bool> DeleteAsync(Guid id, Guid? clientId = null)
    {
        var template = await _db.ReportTemplates
            .FirstOrDefaultAsync(item => item.Id == id && (clientId == null || item.ClientId == null || item.ClientId == clientId));

        if (template is null)
            return false;

        _db.ReportTemplateHistories.Add(BuildHistorySnapshot(template, "Deleted", template.Version + 1));
        _db.ReportTemplates.Remove(template);
        await _db.SaveChangesAsync();
        return true;
    }

    private static ReportTemplateHistory BuildHistorySnapshot(ReportTemplate template, string changeType, int version)
    {
        var snapshot = JsonSerializer.Serialize(new
        {
            template.Id,
            template.ClientId,
            template.Name,
            template.Description,
            template.Instructions,
            template.ExecutionSchemaJson,
            template.DatasetType,
            template.DefaultFormat,
            template.LayoutJson,
            template.FiltersJson,
            template.IsActive,
            Version = version,
            template.CreatedAt,
            template.UpdatedAt,
            template.CreatedBy,
            template.UpdatedBy
        });

        return new ReportTemplateHistory
        {
            Id = IdGenerator.NewId(),
            TemplateId = template.Id,
            Version = version,
            ChangeType = changeType,
            SnapshotJson = snapshot,
            ChangedAt = DateTime.UtcNow,
            ChangedBy = template.UpdatedBy ?? template.CreatedBy
        };
    }
}
