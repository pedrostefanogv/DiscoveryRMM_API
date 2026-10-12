using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Histórico persistido do <c>DatabaseMaintenanceJob</c>. O histórico em memória
/// (JobExecutionHistoryStore) some no restart; esta tabela alimenta a tela de
/// manutenção com a última execução, contadores, tamanho e o que falhou.
/// </summary>
[Migration(20261127_213)]
public class M213_CreateDatabaseMaintenanceRuns : Migration
{
    public override void Up()
    {
        if (Schema.Table("database_maintenance_runs").Exists())
            return;

        Create.Table("database_maintenance_runs")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("started_at_utc").AsCustom("timestamptz").NotNullable()
            .WithColumn("finished_at_utc").AsCustom("timestamptz").NotNullable()
            .WithColumn("succeeded").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("table_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("success_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("error_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("database_size_bytes").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("reclaimed_bytes").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("operations_json").AsCustom("jsonb").NotNullable().WithDefaultValue("[]")
            .WithColumn("error_message").AsString(2000).Nullable();

        Create.Index("ix_database_maintenance_runs_started_at")
            .OnTable("database_maintenance_runs")
            .OnColumn("started_at_utc").Descending();
    }

    public override void Down()
    {
        if (Schema.Table("database_maintenance_runs").Index("ix_database_maintenance_runs_started_at").Exists())
            Delete.Index("ix_database_maintenance_runs_started_at").OnTable("database_maintenance_runs");

        if (Schema.Table("database_maintenance_runs").Exists())
            Delete.Table("database_maintenance_runs");
    }
}
