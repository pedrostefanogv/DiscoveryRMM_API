using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260511_122)]
public class M122_SeedRemoteDebugPermissionForAdmin : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            INSERT INTO permissions (id, resource_type, action_type, description)
            VALUES (gen_random_uuid(), 'RemoteDebug', 'Execute', 'RemoteDebug:Execute')
            ON CONFLICT (resource_type, action_type) DO NOTHING;
        """);

        Execute.Sql("""
            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p
              ON p.resource_type = 'RemoteDebug'
             AND p.action_type = 'Execute'
            WHERE r.name = 'Admin'
              AND r.is_system = true
              AND NOT EXISTS (
                  SELECT 1
                  FROM role_permissions rp
                  WHERE rp.role_id = r.id
                    AND rp.permission_id = p.id
              );
        """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DELETE FROM role_permissions rp
            USING roles r, permissions p
            WHERE rp.role_id = r.id
              AND rp.permission_id = p.id
              AND r.name = 'Admin'
              AND r.is_system = true
              AND p.resource_type = 'RemoteDebug'
              AND p.action_type = 'Execute';
        """);

        Execute.Sql("""
            DELETE FROM permissions p
            WHERE p.resource_type = 'RemoteDebug'
              AND p.action_type = 'Execute'
              AND NOT EXISTS (
                  SELECT 1
                  FROM role_permissions rp
                  WHERE rp.permission_id = p.id
              );
        """);
    }
}
