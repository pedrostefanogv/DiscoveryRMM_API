using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260316_074)]
public class M074_RequireFirstAccessForAllUsers : Migration
{
    public override void Up()
    {
        // A partir desta migration, todo usuário deve concluir onboarding inicial.
        Alter.Column("must_change_password").OnTable("users")
            .AsBoolean().NotNullable().WithDefaultValue(true);

        Alter.Column("must_change_profile").OnTable("users")
            .AsBoolean().NotNullable().WithDefaultValue(true);

        // Aplica a regra também para usuários já existentes.
        Execute.Sql("UPDATE users SET must_change_password = true, must_change_profile = true;");
    }

    public override void Down()
    {
        Alter.Column("must_change_password").OnTable("users")
            .AsBoolean().NotNullable().WithDefaultValue(false);

        Alter.Column("must_change_profile").OnTable("users")
            .AsBoolean().NotNullable().WithDefaultValue(false);
    }
}
