using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Endurecimento de autenticação da área de Identity:
/// - roles.is_active: permite desativar uma role sem excluí-la (contrato do console web).
/// - user_mfa_keys.last_used_step: passo TOTP já consumido, para impedir replay do
///   mesmo código dentro da janela de tolerância.
/// - users.mfa_failed_attempts / users.mfa_lockout_until: limiar de tentativas de MFA
///   por conta, independente do lockout de senha (impede força bruta de OTP quando a
///   senha já é conhecida pelo atacante).
/// </summary>
[Migration(20261028_195)]
public class M195_AddIdentityAuthHardening : Migration
{
    public override void Up()
    {
        if (!Schema.Table("roles").Column("is_active").Exists())
        {
            Alter.Table("roles")
                .AddColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true);
        }

        if (!Schema.Table("user_mfa_keys").Column("last_used_step").Exists())
        {
            Alter.Table("user_mfa_keys")
                .AddColumn("last_used_step").AsInt64().Nullable();
        }

        if (!Schema.Table("users").Column("mfa_failed_attempts").Exists())
        {
            Alter.Table("users")
                .AddColumn("mfa_failed_attempts").AsInt32().NotNullable().WithDefaultValue(0);
        }

        if (!Schema.Table("users").Column("mfa_lockout_until").Exists())
        {
            Alter.Table("users")
                .AddColumn("mfa_lockout_until").AsDateTimeOffset().Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("users").Column("mfa_lockout_until").Exists())
            Delete.Column("mfa_lockout_until").FromTable("users");

        if (Schema.Table("users").Column("mfa_failed_attempts").Exists())
            Delete.Column("mfa_failed_attempts").FromTable("users");

        if (Schema.Table("user_mfa_keys").Column("last_used_step").Exists())
            Delete.Column("last_used_step").FromTable("user_mfa_keys");

        if (Schema.Table("roles").Column("is_active").Exists())
            Delete.Column("is_active").FromTable("roles");
    }
}
