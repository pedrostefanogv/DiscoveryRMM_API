using Discovery.Core.Entities;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.E2E.Tests.Harness;

/// <summary>
/// E2E SQLite in-memory DbContext subclassing DiscoveryDbContext.
/// Ignores Postgres-only Pgvector.Vector? columns and neutralizes Postgres
/// system shadow row-version "xmin" columns so SQLite EnsureCreated() and CRUD work.
/// </summary>
public sealed class SqliteE2EDbContext : DiscoveryDbContext
{
    public SqliteE2EDbContext(DbContextOptions<DiscoveryDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<KnowledgeArticleChunk>(entity =>
        {
            entity.Ignore(e => e.Embedding);
        });

        modelBuilder.Entity<TicketAnswer>(entity =>
        {
            entity.Ignore(e => e.Embedding);
        });

        modelBuilder.Entity<Ticket>(entity =>
        {
            entity.Property<uint>("xmin")
                .ValueGeneratedNever()
                .IsConcurrencyToken(false)
                .HasDefaultValue(0u);
        });

        modelBuilder.Entity<AgentLabelRule>(entity =>
        {
            entity.Property<uint>("xmin")
                .ValueGeneratedNever()
                .IsConcurrencyToken(false)
                .HasDefaultValue(0u);
        });
    }
}
