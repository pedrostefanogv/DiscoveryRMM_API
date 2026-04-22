using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260317_083)]
public class M083_HardenInitialAdminSeed : Migration
{
    // Migration corretiva para ambientes que ja executaram a M073 legada.
    // Reforca first-access e vinculos logicos por login/nome, sem depender de IDs fixos.
    private const string AdminLogin = "admin";
    private const string AdminGroupName = "Administradores";
    private const string AdminRoleName = "Admin";

    public override void Up()
    {
        Execute.Sql($"""
            UPDATE users
            SET
                must_change_password = true,
                must_change_profile = true,
                updated_at = NOW()
            WHERE login = '{AdminLogin}';
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
        // No-op: migration corretiva/idempotente.
    }
}
