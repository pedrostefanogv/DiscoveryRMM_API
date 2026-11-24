using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20261124_298)]
public class M298_FixReportScheduleForeignKeysAndIndexes : Migration
{
    public override void Up()
    {
        // Corrige FKs criadas na M111 sem OnDelete (default RESTRICT no PostgreSQL),
        // que impediam excluir agendamentos ja disparados ou templates vinculados.
        IfDatabase("Postgres").Execute.Sql("""
            ALTER TABLE report_executions
                DROP CONSTRAINT IF EXISTS fk_report_exec_schedule,
                ADD CONSTRAINT fk_report_exec_schedule
                    FOREIGN KEY (schedule_id) REFERENCES report_schedules(id) ON DELETE SET NULL;

            ALTER TABLE report_schedules
                DROP CONSTRAINT IF EXISTS fk_report_schedule_template,
                ADD CONSTRAINT fk_report_schedule_template
                    FOREIGN KEY (template_id) REFERENCES report_templates(id) ON DELETE CASCADE;

            ALTER TABLE report_schedules
                DROP CONSTRAINT IF EXISTS fk_report_schedule_client,
                ADD CONSTRAINT fk_report_schedule_client
                    FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE CASCADE;
            """);

        if (Schema.Table("report_executions").Exists() &&
            !Schema.Table("report_executions").Column("attempt_count").Exists())
        {
            Alter.Table("report_executions")
                .AddColumn("attempt_count").AsInt32().NotNullable().WithDefaultValue(0);
        }

        if (Schema.Table("report_executions").Exists())
        {
            IfDatabase("Postgres").Execute.Sql("""
                CREATE INDEX IF NOT EXISTS ix_report_executions_status_created
                    ON report_executions (status, created_at)
                    WHERE status = 0;

                CREATE INDEX IF NOT EXISTS ix_report_executions_status_started
                    ON report_executions (status, started_at)
                    WHERE status = 1;
                """);
        }
    }

    public override void Down()
    {
        IfDatabase("Postgres").Execute.Sql("""
            DROP INDEX IF EXISTS ix_report_executions_status_started;
            DROP INDEX IF EXISTS ix_report_executions_status_created;
            """);

        if (Schema.Table("report_executions").Exists() &&
            Schema.Table("report_executions").Column("attempt_count").Exists())
        {
            Delete.Column("attempt_count").FromTable("report_executions");
        }
    }
}
