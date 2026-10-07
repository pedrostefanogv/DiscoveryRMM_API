using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Impede atribuições duplicadas de role ao mesmo grupo/escopo. A checagem no handler
/// é TOCTOU (dois admins simultâneos passam pela verificação), então a garantia precisa
/// estar no banco. O índice é sobre a expressão COALESCE(scope_id, ...) porque no
/// PostgreSQL NULLs não colidem em índices únicos comuns.
/// </summary>
[Migration(20261029_196)]
public class M196_UniqueUserGroupRoleAssignment : Migration
{
    private const string NormalizedNullUuid = "'00000000-0000-0000-0000-000000000000'::uuid";

    public override void Up()
    {
        // Remove duplicatas pré-existentes, mantendo a atribuição mais antiga de cada escopo.
        Execute.Sql($@"
            DELETE FROM user_group_roles a
            USING user_group_roles b
            WHERE a.id <> b.id
              AND a.group_id = b.group_id
              AND a.role_id = b.role_id
              AND a.scope_level = b.scope_level
              AND COALESCE(a.scope_id, {NormalizedNullUuid}) = COALESCE(b.scope_id, {NormalizedNullUuid})
              AND (a.assigned_at, a.id) > (b.assigned_at, b.id);
        ");

        Execute.Sql($@"
            CREATE UNIQUE INDEX IF NOT EXISTS ux_user_group_roles_scope
            ON user_group_roles (group_id, role_id, scope_level, COALESCE(scope_id, {NormalizedNullUuid}));
        ");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ux_user_group_roles_scope;");
    }
}
