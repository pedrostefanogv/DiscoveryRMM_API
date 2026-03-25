using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260324_087)]
public class M087_AddNatsEnabled : Migration
{
    public override void Up()
    {
        Alter.Table("server_configurations")
            .AddColumn("nats_enabled").AsBoolean().NotNullable().WithDefaultValue(true);

        // Garante que instâncias existentes já tenham os novos defaults
        Execute.Sql("UPDATE server_configurations SET nats_auth_enabled = true, nats_use_scoped_subjects = true WHERE TRUE;");
    }

    public override void Down()
    {
        Delete.Column("nats_enabled").FromTable("server_configurations");
    }
}
