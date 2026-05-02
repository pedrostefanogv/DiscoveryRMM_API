using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Cria o grupo "Administradores" e vincula o primeiro usuário ativo + role Admin com escopo Global.
///
/// A M069 criou roles/permissões e a M083 tentou vincular ao grupo "Administradores",
/// mas nenhuma migration anterior criava o grupo de fato. O grupo só era criado pelo
/// comando --recover-admin (MaintenanceMode).
///
/// Esta migration fecha o gap: cria o grupo se não existir, vincula qualquer usuário
/// existente e atribui a role Admin com escopo Global.
/// Compatível com qualquer login de admin (admin, admindam0ujgxpx, etc).
/// </summary>
[Migration(20260501_118)]
public class M118_SeedDefaultAdminGroup : Migration
{
    private const string AdminGroupName = "Administradores";
    private const string AdminRoleName = "Admin";

    public override void Up()
    {
        // 1. Cria o grupo "Administradores" se não existir
        Execute.Sql($"""
            INSERT INTO user_groups (id, name, description, is_active, created_at, updated_at)
            SELECT gen_random_uuid(), '{AdminGroupName}', 'Grupo administrativo inicial do sistema', true, NOW(), NOW()
            WHERE NOT EXISTS (
                SELECT 1 FROM user_groups WHERE name = '{AdminGroupName}'
            );
        """);

        // 2. Vincula o PRIMEIRO usuário ativo ao grupo (compatível com qualquer login)
        Execute.Sql($"""
            INSERT INTO user_group_memberships (user_id, group_id, joined_at)
            SELECT u.id, g.id, NOW()
            FROM (SELECT id FROM users WHERE is_active = true ORDER BY created_at LIMIT 1) u
            CROSS JOIN LATERAL (
                SELECT id
                FROM user_groups
                WHERE name = '{AdminGroupName}'
                ORDER BY created_at, id
                LIMIT 1
            ) g
            WHERE NOT EXISTS (
                SELECT 1
                FROM user_group_memberships ugm
                WHERE ugm.user_id = u.id
                  AND ugm.group_id = g.id
            );
        """);

        // 3. Atribui a role Admin ao grupo com escopo Global
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
