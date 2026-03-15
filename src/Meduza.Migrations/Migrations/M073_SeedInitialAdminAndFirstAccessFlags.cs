using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260316_073)]
public class M073_SeedInitialAdminAndFirstAccessFlags : Migration
{
    private const string AdminUserId = "00000000-0000-7001-0000-00000000a001";
    private const string AdminGroupId = "00000000-0000-7001-0000-00000000a101";
    private const string AdminGroupRoleId = "00000000-0000-7001-0000-00000000a201";
    private const string AdminRoleId = "00000000-0000-7001-0000-000000000001";

    // Senha inicial: Mudar@123
    // Hash Argon2id gerado com os mesmos parametros de UserPasswordService.
    private const string AdminPasswordSalt = "UbXXzHv/t1zmKcig2y6tVw==";
    private const string AdminPasswordHash = "h9Qf+17u20UinQP42gG8UXt6G2jlepfRYsj2YK0095g=";

    public override void Up()
    {
        Alter.Table("users")
            .AddColumn("must_change_password").AsBoolean().NotNullable().WithDefaultValue(false);

        Alter.Table("users")
            .AddColumn("must_change_profile").AsBoolean().NotNullable().WithDefaultValue(false);

        Execute.Sql($"""
            INSERT INTO users (
                id, login, email, full_name, password_hash, password_salt,
                is_active, mfa_required, mfa_configured,
                must_change_password, must_change_profile,
                created_at, updated_at
            )
            VALUES (
                '{AdminUserId}',
                'admin',
                'admin@local.meduza',
                'Administrador Inicial',
                '{AdminPasswordHash}',
                '{AdminPasswordSalt}',
                true,
                true,
                false,
                true,
                true,
                NOW(),
                NOW()
            )
            ON CONFLICT (login) DO NOTHING;
        """);

        Execute.Sql($"""
            INSERT INTO user_groups (id, name, description, is_active, created_at, updated_at)
            VALUES (
                '{AdminGroupId}',
                'Administradores',
                'Grupo administrativo inicial do sistema',
                true,
                NOW(),
                NOW()
            )
            ON CONFLICT (id) DO NOTHING;
        """);

        Execute.Sql($"""
            INSERT INTO user_group_memberships (user_id, group_id, joined_at)
            VALUES ('{AdminUserId}', '{AdminGroupId}', NOW())
            ON CONFLICT (user_id, group_id) DO NOTHING;
        """);

        Execute.Sql($"""
            INSERT INTO user_group_roles (id, group_id, role_id, scope_level, scope_id, assigned_at)
            VALUES ('{AdminGroupRoleId}', '{AdminGroupId}', '{AdminRoleId}', 'Global', NULL, NOW())
            ON CONFLICT (id) DO NOTHING;
        """);
    }

    public override void Down()
    {
        Execute.Sql($"DELETE FROM user_group_roles WHERE id = '{AdminGroupRoleId}';");
        Execute.Sql($"DELETE FROM user_group_memberships WHERE user_id = '{AdminUserId}' AND group_id = '{AdminGroupId}';");
        Execute.Sql($"DELETE FROM user_groups WHERE id = '{AdminGroupId}';");
        Execute.Sql($"DELETE FROM users WHERE id = '{AdminUserId}';");

        Delete.Column("must_change_profile").FromTable("users");
        Delete.Column("must_change_password").FromTable("users");
    }
}
