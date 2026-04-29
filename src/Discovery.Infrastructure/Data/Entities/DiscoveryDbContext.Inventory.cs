using Discovery.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

// ── Inventory: SoftwareCatalog, AgentSoftwareInventory, AgentLabels ──

public partial class DiscoveryDbContext
{
    static partial void ConfigureInventory(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SoftwareCatalog>(entity =>
        {
            entity.ToTable("software_catalog");
            entity.HasKey(catalog => catalog.Id);
            entity.HasIndex(catalog => catalog.Fingerprint)
                .IsUnique();

            entity.Property(catalog => catalog.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(catalog => catalog.Name)
                .HasColumnName("name")
                .HasMaxLength(300);
            entity.Property(catalog => catalog.Publisher)
                .HasColumnName("publisher")
                .HasMaxLength(300);
            entity.Property(catalog => catalog.InstallId)
                .HasColumnName("install_id")
                .HasMaxLength(1000);
            entity.Property(catalog => catalog.Serial)
                .HasColumnName("serial")
                .HasMaxLength(1000);
            entity.Property(catalog => catalog.Source)
                .HasColumnName("source")
                .HasMaxLength(120);
            entity.Property(catalog => catalog.Fingerprint)
                .HasColumnName("fingerprint")
                .HasMaxLength(64);
            entity.Property(catalog => catalog.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(catalog => catalog.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");
        });

        modelBuilder.Entity<AgentSoftwareInventory>(entity =>
        {
            entity.ToTable("agent_software_inventory");
            entity.HasKey(inventory => inventory.Id);
            entity.HasIndex(inventory => new { inventory.AgentId, inventory.SoftwareId })
                .HasDatabaseName("ix_agent_software_inventory_agent_software");
            entity.HasIndex(inventory => new { inventory.AgentId, inventory.IsPresent })
                .HasDatabaseName("ix_agent_software_inventory_agent_present");

            entity.Property(inventory => inventory.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(inventory => inventory.AgentId)
                .HasColumnName("agent_id");
            entity.Property(inventory => inventory.SoftwareId)
                .HasColumnName("software_id");
            entity.Property(inventory => inventory.CollectedAt)
                .HasColumnName("collected_at")
                .HasColumnType("timestamptz");
            entity.Property(inventory => inventory.FirstSeenAt)
                .HasColumnName("first_seen_at")
                .HasColumnType("timestamptz");
            entity.Property(inventory => inventory.LastSeenAt)
                .HasColumnName("last_seen_at")
                .HasColumnType("timestamptz");
            entity.Property(inventory => inventory.Version)
                .HasColumnName("version")
                .HasMaxLength(120);
            entity.Property(inventory => inventory.IsPresent)
                .HasColumnName("is_present");
            entity.Property(inventory => inventory.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz");
            entity.Property(inventory => inventory.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz");

            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(inventory => inventory.AgentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SoftwareCatalog>()
                .WithMany()
                .HasForeignKey(inventory => inventory.SoftwareId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentLabelRule>(entity =>
        {
            entity.ToTable("agent_label_rules");
            entity.HasKey(rule => rule.Id);
            entity.HasIndex(rule => rule.IsEnabled).HasDatabaseName("ix_agent_label_rules_is_enabled");
            entity.HasIndex(rule => rule.Label).HasDatabaseName("ix_agent_label_rules_label");

            entity.Property(rule => rule.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(rule => rule.Name).HasColumnName("name").HasMaxLength(200);
            entity.Property(rule => rule.Label).HasColumnName("label").HasMaxLength(120);
            entity.Property(rule => rule.Description).HasColumnName("description").HasMaxLength(2000);
            entity.Property(rule => rule.IsEnabled).HasColumnName("is_enabled");
            entity.Property(rule => rule.ApplyMode).HasColumnName("apply_mode").HasConversion<int>();
            entity.Property(rule => rule.ExpressionJson).HasColumnName("expression_json").HasColumnType("jsonb");
            entity.Property(rule => rule.CreatedBy).HasColumnName("created_by").HasMaxLength(256);
            entity.Property(rule => rule.UpdatedBy).HasColumnName("updated_by").HasMaxLength(256);
            entity.Property(rule => rule.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(rule => rule.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<AgentLabel>(entity =>
        {
            entity.ToTable("agent_labels");
            entity.HasKey(label => label.Id);
            entity.HasIndex(label => new { label.AgentId, label.Label })
                .IsUnique()
                .HasDatabaseName("ux_agent_labels_agent_label");

            entity.Property(label => label.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(label => label.AgentId).HasColumnName("agent_id");
            entity.Property(label => label.Label).HasColumnName("label").HasMaxLength(120);
            entity.Property(label => label.SourceType).HasColumnName("source_type").HasConversion<int>();
            entity.Property(label => label.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            entity.Property(label => label.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");

            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(label => label.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentLabelRuleMatch>(entity =>
        {
            entity.ToTable("agent_label_rule_matches");
            entity.HasKey(match => match.Id);
            entity.HasIndex(match => new { match.RuleId, match.AgentId })
                .IsUnique()
                .HasDatabaseName("ux_agent_label_rule_matches_rule_agent");
            entity.HasIndex(match => new { match.AgentId, match.Label })
                .HasDatabaseName("ix_agent_label_rule_matches_agent_label");

            entity.Property(match => match.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(match => match.RuleId).HasColumnName("rule_id");
            entity.Property(match => match.AgentId).HasColumnName("agent_id");
            entity.Property(match => match.Label).HasColumnName("label").HasMaxLength(120);
            entity.Property(match => match.MatchedAt).HasColumnName("matched_at").HasColumnType("timestamptz");
            entity.Property(match => match.LastEvaluatedAt).HasColumnName("last_evaluated_at").HasColumnType("timestamptz");

            entity.HasOne<AgentLabelRule>()
                .WithMany()
                .HasForeignKey(match => match.RuleId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(match => match.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
