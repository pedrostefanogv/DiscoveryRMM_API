using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260307_035)]
public class M035_ReportExecutionClientScopeOptional : Migration
{
    public override void Up()
    {
        Delete.ForeignKey("FK_report_executions_client_id_clients_id").OnTable("report_executions");

        Alter.Table("report_executions")
            .AlterColumn("client_id")
            .AsGuid()
            .Nullable();

        Create.ForeignKey("FK_report_executions_client_id_clients_id")
            .FromTable("report_executions").ForeignColumn("client_id")
            .ToTable("clients").PrimaryColumn("id")
            .OnDeleteOrUpdate(System.Data.Rule.SetNull);
    }

    public override void Down()
    {
        Delete.ForeignKey("FK_report_executions_client_id_clients_id").OnTable("report_executions");

        Execute.Sql("DELETE FROM report_executions WHERE client_id IS NULL;");

        Alter.Table("report_executions")
            .AlterColumn("client_id")
            .AsGuid()
            .NotNullable();

        Create.ForeignKey("FK_report_executions_client_id_clients_id")
            .FromTable("report_executions").ForeignColumn("client_id")
            .ToTable("clients").PrimaryColumn("id")
            .OnDeleteOrUpdate(System.Data.Rule.Cascade);
    }
}
