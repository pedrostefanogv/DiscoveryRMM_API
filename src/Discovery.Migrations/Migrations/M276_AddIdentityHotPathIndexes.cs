using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Índices do caminho quente de autorização de identidade.
///
/// A consulta que resolve as permissões efetivas de um usuário faz join de
/// user_group_memberships -> user_group_roles -> roles -> role_permissions ->
/// permissions (UserGroupRepository.GetRolesWithPermissionsForUserAsync). Ela roda a
/// cada cache miss do PermissionService (TTL 5 min, ou a cada invalidação).
///
/// Até aqui, o único índice de user_group_roles era o UNIQUE da M196, cuja primeira
/// coluna é group_id — o que não ajuda o join por role_id nem a filtragem por escopo
/// usada agora na listagem escopada de usuários.
///
/// Tudo é IF NOT EXISTS: bancos que já receberam o índice por outra via não quebram.
/// </summary>
[Migration(20270110_276)]
public class M276_AddIdentityHotPathIndexes : Migration
{
    public override void Up()
    {
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_user_group_roles_group_id
            ON user_group_roles (group_id);
        ");
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_user_group_roles_role_id
            ON user_group_roles (role_id);
        ");
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_role_permissions_permission_id
            ON role_permissions (permission_id);
        ");
        // Listagem/contagem escopada de usuários filtra por ativo em vários fluxos.
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_users_is_active
            ON users (is_active);
        ");
        // Filtro de escopo da listagem escopada (scope_level + scope_id).
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_user_group_roles_scope_level_scope_id
            ON user_group_roles (scope_level, scope_id);
        ");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ix_user_group_roles_group_id;");
        Execute.Sql("DROP INDEX IF EXISTS ix_user_group_roles_role_id;");
        Execute.Sql("DROP INDEX IF EXISTS ix_role_permissions_permission_id;");
        Execute.Sql("DROP INDEX IF EXISTS ix_users_is_active;");
        Execute.Sql("DROP INDEX IF EXISTS ix_user_group_roles_scope_level_scope_id;");
    }
}
