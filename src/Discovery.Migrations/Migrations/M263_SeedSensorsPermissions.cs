using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Semeia as permissões do recurso Sensors (aba "Sensores" do detalhe do agente)
/// e concede aos papéis padrão. Mesmo padrão da M231 (Patches).
/// </summary>
[Migration(20270101_263)]
public class M263_SeedSensorsPermissions : Migration
{
    public override void Up()
    {
        Execute.Sql(@"
            INSERT INTO permissions (id, resource_type, action_type, description)
            SELECT gen_random_uuid(), v.resource_type, v.action_type,
                   v.resource_type || ':' || v.action_type
            FROM (VALUES
                ('Sensors','View'), ('Sensors','Create'), ('Sensors','Edit'),
                ('Sensors','Delete'), ('Sensors','Execute')
            ) AS v(resource_type, action_type)
            ON CONFLICT (resource_type, action_type) DO NOTHING;
        ");

        Grant("Admin", allActions: true);
        Grant("Manager", allActions: true);
        // Operator: vê, edita e executa (não exclui definições).
        Grant("Operator", allActions: false, execute: true);
        Grant("Support", allActions: false);
        Grant("Viewer", allActions: false);
    }

    private void Grant(string roleName, bool allActions, bool execute = false)
    {
        var actionFilter = allActions
            ? string.Empty
            : execute
                ? " AND p.action_type IN ('View','Create','Edit','Execute')"
                : " AND p.action_type = 'View'";
        Execute.Sql($@"
            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            CROSS JOIN permissions p
            WHERE r.name = '{roleName}'
              AND r.is_system = true
              AND p.resource_type = 'Sensors'{actionFilter}
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
              AND p.resource_type = 'Sensors';
        ");
        Execute.Sql(@"
            DELETE FROM permissions p
            WHERE p.resource_type = 'Sensors'
              AND NOT EXISTS (SELECT 1 FROM role_permissions rp WHERE rp.permission_id = p.id);
        ");
    }
}
