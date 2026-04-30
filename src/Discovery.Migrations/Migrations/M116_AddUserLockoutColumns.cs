using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260430_116)]
public class M116_AddUserLockoutColumns : Migration
{
    public override void Up()
    {
        Alter.Table("users")
            .AddColumn("failed_login_attempts").AsInt32().NotNullable().WithDefaultValue(0);

        Alter.Table("users")
            .AddColumn("lockout_until").AsCustom("timestamptz").Nullable();
    }

    public override void Down()
    {
        Delete.Column("failed_login_attempts").FromTable("users");
        Delete.Column("lockout_until").FromTable("users");
    }
}
