using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Remove colunas legadas de armazenamento local de relatórios.
/// Mantém apenas o modelo baseado em Object Storage (S3-compatível).
/// </summary>
[Migration(20260312_062)]
public class M062_DropLegacyLocalReportColumns : Migration
{
    public override void Up()
    {
        if (Schema.Table("report_executions").Column("result_path").Exists())
            Delete.Column("result_path").FromTable("report_executions");

        if (Schema.Table("report_executions").Column("result_content_type").Exists())
            Delete.Column("result_content_type").FromTable("report_executions");

        if (Schema.Table("report_executions").Column("result_size_bytes").Exists())
            Delete.Column("result_size_bytes").FromTable("report_executions");

        if (Schema.Table("server_configurations").Column("object_storage_provider_type").Exists())
            Delete.Column("object_storage_provider_type").FromTable("server_configurations");
    }

    public override void Down()
    {
        if (!Schema.Table("report_executions").Column("result_path").Exists())
            Alter.Table("report_executions").AddColumn("result_path").AsString(500).Nullable();

        if (!Schema.Table("report_executions").Column("result_content_type").Exists())
            Alter.Table("report_executions").AddColumn("result_content_type").AsString(100).Nullable();

        if (!Schema.Table("report_executions").Column("result_size_bytes").Exists())
            Alter.Table("report_executions").AddColumn("result_size_bytes").AsInt64().Nullable();

        if (!Schema.Table("server_configurations").Column("object_storage_provider_type").Exists())
            Alter.Table("server_configurations").AddColumn("object_storage_provider_type").AsInt32().NotNullable().WithDefaultValue(5);
    }
}
