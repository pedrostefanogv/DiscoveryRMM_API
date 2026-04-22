using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

/// <summary>
/// Seed de dados iniciais: estados globais de workflow com transições padrão.
/// </summary>
public static class DatabaseSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkflowRepository>();

        var existing = await repo.GetStatesAsync(null);
        if (existing.Any()) return; // Já possui estados globais

        // Criar estados globais default
        var open = await repo.CreateStateAsync(new WorkflowState
        {
            Id = IdGenerator.NewId(), Name = "Open", Color = "#3b82f6",
            IsInitial = true, IsFinal = false, SortOrder = 1
        });
        var inProgress = await repo.CreateStateAsync(new WorkflowState
        {
            Id = IdGenerator.NewId(), Name = "In Progress", Color = "#f59e0b",
            IsInitial = false, IsFinal = false, SortOrder = 2
        });
        var waiting = await repo.CreateStateAsync(new WorkflowState
        {
            Id = IdGenerator.NewId(), Name = "Waiting on Client", Color = "#8b5cf6",
            IsInitial = false, IsFinal = false, SortOrder = 3
        });
        var resolved = await repo.CreateStateAsync(new WorkflowState
        {
            Id = IdGenerator.NewId(), Name = "Resolved", Color = "#10b981",
            IsInitial = false, IsFinal = true, SortOrder = 4
        });
        var closed = await repo.CreateStateAsync(new WorkflowState
        {
            Id = IdGenerator.NewId(), Name = "Closed", Color = "#6b7280",
            IsInitial = false, IsFinal = true, SortOrder = 5
        });

        // Transições padrão
        var transitions = new (WorkflowState From, WorkflowState To, string Name)[]
        {
            (open, inProgress, "Start Working"),
            (open, closed, "Close"),
            (inProgress, waiting, "Wait for Client"),
            (inProgress, resolved, "Resolve"),
            (inProgress, open, "Reopen"),
            (waiting, inProgress, "Resume"),
            (waiting, closed, "Close"),
            (resolved, closed, "Close"),
            (resolved, open, "Reopen"),
            (closed, open, "Reopen"),
        };

        foreach (var (from, to, name) in transitions)
        {
            await repo.CreateTransitionAsync(new WorkflowTransition
            {
                Id = IdGenerator.NewId(),
                FromStateId = from.Id,
                ToStateId = to.Id,
                Name = name
            });
        }
    }
}
