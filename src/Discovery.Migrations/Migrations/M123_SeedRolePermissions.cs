using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260511_123)]
public class M123_SeedRolePermissions : Migration
{
    public override void Up()
    {
        // Manager: Gerencia clientes e sites + aprova apps/automacoes no escopo
        SeedPermissionsForRole("Manager", new[] {
            ("Clients", "View"), ("Clients", "Create"), ("Clients", "Edit"), ("Clients", "Delete"),
            ("Sites", "View"), ("Sites", "Create"), ("Sites", "Edit"), ("Sites", "Delete"),
            ("Reports", "View"), ("Reports", "Create"),
            ("Dashboard", "View"),
            ("Agents", "View"), ("Agents", "Create"), ("Agents", "Edit"), ("Agents", "Delete"),
            ("Tickets", "View"),
            ("Deployment", "View"),
            ("Logs", "View"),
            ("Automation", "View"), ("Automation", "Create"), ("Automation", "Edit"), ("Automation", "Execute"),
            ("AppStore", "View"), ("AppStore", "Edit")
        });

        // Operator: Opera tickets e agentes
        SeedPermissionsForRole("Operator", new[] {
            ("Tickets", "View"), ("Tickets", "Create"), ("Tickets", "Edit"),
            ("Agents", "View"), ("Agents", "Create"), ("Agents", "Edit"),
            ("Automation", "View"), ("Automation", "Create"), ("Automation", "Edit"), ("Automation", "Execute"),
            ("RemoteDebug", "Execute"),
            ("Dashboard", "View"),
            ("Reports", "View"),
            ("Deployment", "View"),
            ("Logs", "View")
        });

        // Support: Acesso de suporte (tickets completos no escopo + leitura geral)
        SeedPermissionsForRole("Support", new[] {
            ("Tickets", "View"), ("Tickets", "Create"), ("Tickets", "Edit"), ("Tickets", "Delete"),
            ("Agents", "View"),
            ("Clients", "View"),
            ("Sites", "View"),
            ("Dashboard", "View"),
            ("Reports", "View"),
            ("Logs", "View"),
            ("KnowledgeBase", "View")
        });

        // Viewer: Apenas visualizacao de dashboards
        SeedPermissionsForRole("Viewer", new[] {
            ("Dashboard", "View"),
            ("Reports", "View")
        });
    }

    private void SeedPermissionsForRole(string roleName, (string Resource, string Action)[] permissions)
    {
        var valuesList = string.Join(", ", permissions
            .Select(p => $"('{p.Resource}', '{p.Action}')"));

        Execute.Sql($@"
            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON (p.resource_type, p.action_type) IN ({valuesList})
            WHERE r.name = '{roleName}'
              AND r.is_system = true
              AND NOT EXISTS (
                  SELECT 1
                  FROM role_permissions rp
                  WHERE rp.role_id = r.id
                    AND rp.permission_id = p.id
              );
        ");
    }

    public override void Down()
    {
        foreach (var roleName in new[] { "Manager", "Operator", "Support", "Viewer" })
        {
            Execute.Sql($@"
                DELETE FROM role_permissions rp
                USING roles r
                WHERE rp.role_id = r.id
                  AND r.name = '{roleName}'
                  AND r.is_system = true;
            ");
        }
    }
}
