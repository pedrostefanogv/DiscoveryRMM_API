using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260316_069)]
public class M069_CreateUsersIdentity : Migration
{
    public override void Up()
    {
        // ── users ──────────────────────────────────────────────────────────────
        Create.Table("users")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("login").AsString(100).NotNullable().Unique("ix_users_login")
            .WithColumn("email").AsString(256).NotNullable().Unique("ix_users_email")
            .WithColumn("full_name").AsString(256).NotNullable()
            .WithColumn("password_hash").AsString(256).NotNullable()
            .WithColumn("password_salt").AsString(64).NotNullable()
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("mfa_required").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("mfa_configured").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("last_login_at").AsCustom("timestamptz").Nullable();

        // ── user_groups ────────────────────────────────────────────────────────
        Create.Table("user_groups")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("description").AsString(1000).Nullable()
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable();

        // ── user_group_memberships ─────────────────────────────────────────────
        Create.Table("user_group_memberships")
            .WithColumn("user_id").AsGuid().NotNullable()
                .ForeignKey("fk_ugm_user", "users", "id")
            .WithColumn("group_id").AsGuid().NotNullable()
                .ForeignKey("fk_ugm_group", "user_groups", "id")
            .WithColumn("joined_at").AsCustom("timestamptz").NotNullable()
                .WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.PrimaryKey("pk_user_group_memberships")
            .OnTable("user_group_memberships")
            .Columns("user_id", "group_id");

        // ── roles ──────────────────────────────────────────────────────────────
        Create.Table("roles")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("description").AsString(1000).Nullable()
            .WithColumn("type").AsString(32).NotNullable().WithDefaultValue("Custom")
            .WithColumn("is_system").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable();

        // ── permissions ────────────────────────────────────────────────────────
        Create.Table("permissions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("resource_type").AsString(64).NotNullable()
            .WithColumn("action_type").AsString(32).NotNullable()
            .WithColumn("description").AsString(500).Nullable();

        Create.Index("ix_permissions_resource_action")
            .OnTable("permissions")
            .OnColumn("resource_type").Ascending()
            .OnColumn("action_type").Ascending()
            .WithOptions().Unique();

        // ── role_permissions ───────────────────────────────────────────────────
        Create.Table("role_permissions")
            .WithColumn("role_id").AsGuid().NotNullable()
                .ForeignKey("fk_rp_role", "roles", "id")
            .WithColumn("permission_id").AsGuid().NotNullable()
                .ForeignKey("fk_rp_permission", "permissions", "id");

        Create.PrimaryKey("pk_role_permissions")
            .OnTable("role_permissions")
            .Columns("role_id", "permission_id");

        // ── user_group_roles ───────────────────────────────────────────────────
        Create.Table("user_group_roles")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("group_id").AsGuid().NotNullable()
                .ForeignKey("fk_ugr_group", "user_groups", "id")
            .WithColumn("role_id").AsGuid().NotNullable()
                .ForeignKey("fk_ugr_role", "roles", "id")
            .WithColumn("scope_level").AsString(32).NotNullable()
            .WithColumn("scope_id").AsGuid().Nullable()
            .WithColumn("assigned_at").AsCustom("timestamptz").NotNullable()
                .WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_user_group_roles_group_role")
            .OnTable("user_group_roles")
            .OnColumn("group_id").Ascending()
            .OnColumn("role_id").Ascending()
            .OnColumn("scope_level").Ascending();

        // ── seed: system roles ─────────────────────────────────────────────────
        var now = "NOW()";
        Execute.Sql($@"
            INSERT INTO roles (id, name, description, type, is_system, created_at, updated_at) VALUES
            ('00000000-0000-7001-0000-000000000001', 'Admin',    'Acesso total ao sistema',              'System', true, {now}, {now}),
            ('00000000-0000-7001-0000-000000000002', 'Manager',  'Gerencia clientes e sites',            'System', true, {now}, {now}),
            ('00000000-0000-7001-0000-000000000003', 'Operator', 'Opera tickets e agentes',              'System', true, {now}, {now}),
            ('00000000-0000-7001-0000-000000000004', 'Support',  'Acesso de suporte (somente leitura)',  'System', true, {now}, {now}),
            ('00000000-0000-7001-0000-000000000005', 'Viewer',   'Apenas visualizacao de dashboards',   'System', true, {now}, {now});
        ");

        // ── seed: all permissions (ResourceType × ActionType) ─────────────────
        Execute.Sql(@"
            INSERT INTO permissions (id, resource_type, action_type, description)
            SELECT gen_random_uuid(), r.resource_type, a.action_type,
                   r.resource_type || ':' || a.action_type
            FROM (VALUES
                ('Agents'), ('Tickets'), ('Clients'), ('Sites'), ('Reports'),
                ('ServerConfig'), ('ClientConfig'), ('SiteConfig'), ('Users'),
                ('Automation'), ('Deployment'), ('KnowledgeBase'), ('AiChat'),
                ('AppStore'), ('Logs'), ('Dashboard')
            ) AS r(resource_type)
            CROSS JOIN (VALUES
                ('View'), ('Create'), ('Edit'), ('Delete'), ('Execute')
            ) AS a(action_type);
        ");

        // ── seed: Admin role gets all permissions ─────────────────────────────
        Execute.Sql(@"
            INSERT INTO role_permissions (role_id, permission_id)
            SELECT '00000000-0000-7001-0000-000000000001', id FROM permissions;
        ");
    }

    public override void Down()
    {
        Delete.Table("user_group_roles");
        Delete.Table("role_permissions");
        Delete.Table("permissions");
        Delete.Table("roles");
        Delete.Table("user_group_memberships");
        Delete.Table("user_groups");
        Delete.Table("users");
    }
}
