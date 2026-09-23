using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Semeia as permissões dos recursos de configuração de suporte (Workflow, Sla,
/// Departments) e concede aos papéis padrão. Sem isso, o RBAC novo bloquearia
/// até o Admin, que recebeu "todas as permissões" no M069 (antes destes recursos).
/// </summary>
[Migration(20260923_157)]
public class M157_SeedSupportConfigPermissions : Migration
{
    public override void Up()
    {
        Execute.Sql(@"
            INSERT INTO permissions (id, resource_type, action_type, description)
            SELECT gen_random_uuid(), v.resource_type, v.action_type,
                   v.resource_type || ':' || v.action_type
            FROM (VALUES
                ('Workflow','View'), ('Workflow','Create'), ('Workflow','Edit'), ('Workflow','Delete'),
                ('Sla','View'), ('Sla','Create'), ('Sla','Edit'), ('Sla','Delete'),
                ('Departments','View'), ('Departments','Create'), ('Departments','Edit'), ('Departments','Delete')
            ) AS v(resource_type, action_type)
            ON CONFLICT (resource_type, action_type) DO NOTHING;
        ");

        // Admin e Manager: controle total da configuração de suporte.
        GrantAll("Admin", allActions: true);
        GrantAll("Manager", allActions: true);
        // Demais papéis: somente leitura por padrão (o Admin pode conceder mais).
        GrantAll("Operator", allActions: false);
        GrantAll("Support", allActions: false);
        GrantAll("Viewer", allActions: false);
    }

    private void GrantAll(string roleName, bool allActions)
    {
        var actionFilter = allActions ? string.Empty : " AND p.action_type = 'View'";
        Execute.Sql($@"
            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            CROSS JOIN permissions p
            WHERE r.name = '{roleName}'
              AND r.is_system = true
              AND p.resource_type IN ('Workflow','Sla','Departments'){actionFilter}
              AND NOT EXISTS (
                  SELECT 1 FROM role_permissions rp
                  WHERE rp.role_id = r.id AND rp.permission_id = p.id
              );
        ");
    }

    public override void Down()
    {
        Execute.Sql(@"
            DELETE FROM role_permissions rp
            USING permissions p
            WHERE rp.permission_id = p.id
              AND p.resource_type IN ('Workflow','Sla','Departments');
        ");
        Execute.Sql(@"
            DELETE FROM permissions p
            WHERE p.resource_type IN ('Workflow','Sla','Departments')
              AND NOT EXISTS (SELECT 1 FROM role_permissions rp WHERE rp.permission_id = p.id);
        ");
    }
}
