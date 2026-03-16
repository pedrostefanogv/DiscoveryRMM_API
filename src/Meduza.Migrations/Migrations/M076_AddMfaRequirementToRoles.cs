using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260316_076)]
public class M076_AddMfaRequirementToRoles : Migration
{
    public override void Up()
    {
        Alter.Table("roles")
            .AddColumn("mfa_requirement").AsString(32).NotNullable().WithDefaultValue("None");

        // Defaults para roles de sistema no padrão solicitado.
        Execute.Sql("UPDATE roles SET mfa_requirement = 'Fido2' WHERE name = 'Admin';");
        Execute.Sql("UPDATE roles SET mfa_requirement = 'Totp' WHERE name = 'Operator';");
        Execute.Sql("UPDATE roles SET mfa_requirement = 'None' WHERE name = 'Support';");
    }

    public override void Down()
    {
        Delete.Column("mfa_requirement").FromTable("roles");
    }
}
