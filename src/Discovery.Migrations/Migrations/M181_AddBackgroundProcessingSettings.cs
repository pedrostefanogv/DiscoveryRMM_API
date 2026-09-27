using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Processamento periódico configurável: configuração global/cliente dos ciclos
/// (métricas de atendente e triagem por IA), estado dos ciclos por escopo e
/// índices que sustentam a descoberta de alvos em escala.
///
/// Tudo aditivo com defaults: o comportamento atual é preservado (métricas a
/// cada 15 min e triagem com fast lane na abertura).
/// </summary>
[Migration(20261003_181)]
public class M181_AddBackgroundProcessingSettings : Migration
{
    public override void Up()
    {
        AddSettingsColumns();
        CreateProcessingScopeState();
        AddAssigneeIndex();
        AddDecisionMetricsAgeColumn();
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ix_tickets_assigned_user_created;");

        if (Schema.Table("ticket_assignment_decisions").Exists()
            && Schema.Table("ticket_assignment_decisions").Column("metrics_snapshot_age_minutes").Exists())
        {
            Delete.Column("metrics_snapshot_age_minutes").FromTable("ticket_assignment_decisions");
        }

        if (Schema.Table("processing_scope_state").Exists()) Delete.Table("processing_scope_state");

        if (Schema.Table("client_configurations").Exists()
            && Schema.Table("client_configurations").Column("background_processing_settings_json").Exists())
        {
            Delete.Column("background_processing_settings_json").FromTable("client_configurations");
        }

        if (Schema.Table("server_configurations").Exists()
            && Schema.Table("server_configurations").Column("background_processing_settings_json").Exists())
        {
            Delete.Column("background_processing_settings_json").FromTable("server_configurations");
        }
    }

    private void AddSettingsColumns()
    {
        if (Schema.Table("server_configurations").Exists()
            && !Schema.Table("server_configurations").Column("background_processing_settings_json").Exists())
        {
            Alter.Table("server_configurations")
                .AddColumn("background_processing_settings_json").AsCustom("jsonb").NotNullable().WithDefaultValue("{}");
        }

        if (Schema.Table("client_configurations").Exists()
            && !Schema.Table("client_configurations").Column("background_processing_settings_json").Exists())
        {
            Alter.Table("client_configurations")
                .AddColumn("background_processing_settings_json").AsCustom("jsonb").Nullable();
        }
    }

    private void CreateProcessingScopeState()
    {
        if (Schema.Table("processing_scope_state").Exists()) return;

        Create.Table("processing_scope_state")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("scope_type").AsString(40).NotNullable()
            .WithColumn("scope_id").AsGuid().NotNullable()
            .WithColumn("last_run_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("last_result_json").AsCustom("jsonb").Nullable()
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable();

        Create.Index("ux_processing_scope_state_type_scope")
            .OnTable("processing_scope_state")
            .OnColumn("scope_type").Ascending()
            .OnColumn("scope_id").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_processing_scope_state_last_run")
            .OnTable("processing_scope_state")
            .OnColumn("last_run_at").Ascending();
    }

    /// <summary>
    /// Índice parcial que sustenta a descoberta de alvos (por responsável) e a
    /// agregação das métricas. Sem ele, a varredura por cliente em tenants grandes
    /// vira full scan em tickets.
    /// </summary>
    private void AddAssigneeIndex()
    {
        if (!Schema.Table("tickets").Exists()) return;

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_tickets_assigned_user_created
                ON tickets (assigned_to_user_id, created_at DESC)
                WHERE deleted_at IS NULL AND assigned_to_user_id IS NOT NULL;");
    }

    private void AddDecisionMetricsAgeColumn()
    {
        if (!Schema.Table("ticket_assignment_decisions").Exists()) return;

        if (!Schema.Table("ticket_assignment_decisions").Column("metrics_snapshot_age_minutes").Exists())
        {
            Alter.Table("ticket_assignment_decisions")
                .AddColumn("metrics_snapshot_age_minutes").AsInt32().Nullable();
        }
    }
}
