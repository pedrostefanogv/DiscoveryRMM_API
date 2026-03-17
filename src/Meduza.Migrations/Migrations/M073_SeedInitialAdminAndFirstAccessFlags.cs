using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260316_073)]
public class M073_SeedInitialAdminAndFirstAccessFlags : Migration
{
    // Senha inicial: Mudar@123
    // Hash Argon2id gerado com os mesmos parametros de UserPasswordService.
    private const string AdminLogin = "admin";
    private const string AdminEmail = "admin@local.meduza";
    private const string AdminFullName = "Administrador Inicial";
    private const string AdminGroupName = "Administradores";
    private const string AdminRoleName = "Admin";
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
            SELECT
                gen_random_uuid(),
                '{AdminLogin}',
                '{AdminEmail}',
                '{AdminFullName}',
                '{AdminPasswordHash}',
                '{AdminPasswordSalt}',
                true,
                true,
                false,
                true,
                true,
                NOW(),
                NOW()
            WHERE NOT EXISTS (
                SELECT 1 FROM users WHERE login = '{AdminLogin}'
            );
        """);

        Execute.Sql($"""
            INSERT INTO user_groups (id, name, description, is_active, created_at, updated_at)
            SELECT
                gen_random_uuid(),
                '{AdminGroupName}',
                'Grupo administrativo inicial do sistema',
                true,
                NOW(),
                NOW()
            WHERE NOT EXISTS (
                SELECT 1 FROM user_groups WHERE name = '{AdminGroupName}'
            );
        """);

        Execute.Sql($"""
            INSERT INTO user_group_memberships (user_id, group_id, joined_at)
            SELECT u.id, g.id, NOW()
            FROM users u
            CROSS JOIN LATERAL (
                SELECT id
                FROM user_groups
                WHERE name = '{AdminGroupName}'
                ORDER BY created_at, id
                LIMIT 1
            ) g
            WHERE u.login = '{AdminLogin}'
              AND NOT EXISTS (
                  SELECT 1
                  FROM user_group_memberships ugm
                  WHERE ugm.user_id = u.id
                    AND ugm.group_id = g.id
              );
        """);

        Execute.Sql($"""
            INSERT INTO user_group_roles (id, group_id, role_id, scope_level, scope_id, assigned_at)
            SELECT gen_random_uuid(), g.id, r.id, 'Global', NULL, NOW()
            FROM (
                SELECT id
                FROM user_groups
                WHERE name = '{AdminGroupName}'
                ORDER BY created_at, id
                LIMIT 1
            ) g
            CROSS JOIN (
                SELECT id
                FROM roles
                WHERE name = '{AdminRoleName}'
                ORDER BY is_system DESC, created_at, id
                LIMIT 1
            ) r
            WHERE NOT EXISTS (
                SELECT 1
                FROM user_group_roles ugr
                WHERE ugr.group_id = g.id
                  AND ugr.role_id = r.id
                  AND ugr.scope_level = 'Global'
                  AND ugr.scope_id IS NULL
            );
        """);
    }

    public override void Down()
    {
        Execute.Sql($"""
            DELETE FROM user_group_roles ugr
            USING user_groups g, roles r
            WHERE g.name = '{AdminGroupName}'
              AND r.name = '{AdminRoleName}'
              AND ugr.group_id = g.id
              AND ugr.role_id = r.id
              AND ugr.scope_level = 'Global'
              AND ugr.scope_id IS NULL;
        """);

        Execute.Sql($"""
            DELETE FROM user_group_memberships ugm
            USING users u, user_groups g
            WHERE u.login = '{AdminLogin}'
              AND g.name = '{AdminGroupName}'
              AND ugm.user_id = u.id
              AND ugm.group_id = g.id;
        """);

        Execute.Sql($"DELETE FROM user_groups WHERE name = '{AdminGroupName}';");
        Execute.Sql($"DELETE FROM users WHERE login = '{AdminLogin}';");

        Delete.Column("must_change_profile").FromTable("users");
        Delete.Column("must_change_password").FromTable("users");
    }
}
