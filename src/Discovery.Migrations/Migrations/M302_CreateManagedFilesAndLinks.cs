using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Cria as tabelas da Central de Arquivos S3 (managed_files, file_share_links,
/// file_share_link_items, file_upload_links, file_access_logs), adiciona flags de
/// configuração do portal público /download e aba Arquivos no Agent (Global → Client → Site),
/// e registra as permissões RBAC de <c>ResourceType.Files</c>.
/// </summary>
[Migration(20270129_302)]
public class M302_CreateManagedFilesAndLinks : Migration
{
    public override void Up()
    {
        if (!Schema.Table("file_upload_links").Exists())
        {
            Create.Table("file_upload_links")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("short_code").AsString(32).NotNullable().Unique("uq_file_upload_links_short_code")
                .WithColumn("title").AsString(255).NotNullable()
                .WithColumn("description").AsString(4000).Nullable()
                .WithColumn("client_id").AsGuid().Nullable().ForeignKey("fk_file_upload_links_client", "clients", "id").OnDelete(System.Data.Rule.SetNull)
                .WithColumn("site_id").AsGuid().Nullable().ForeignKey("fk_file_upload_links_site", "sites", "id").OnDelete(System.Data.Rule.SetNull)
                .WithColumn("target_folder_path").AsString(500).NotNullable().WithDefaultValue("/recebidos")
                .WithColumn("require_passcode").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("passcode_hash").AsString(512).Nullable()
                .WithColumn("failed_passcode_attempts").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("locked_until_utc").AsDateTimeOffset().Nullable()
                .WithColumn("expires_at_utc").AsDateTimeOffset().NotNullable()
                .WithColumn("allow_multiple").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("max_files").AsInt32().NotNullable().WithDefaultValue(10)
                .WithColumn("uploaded_files_count").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("max_file_size_bytes").AsInt64().NotNullable().WithDefaultValue(104857600L)
                .WithColumn("max_total_size_bytes").AsInt64().NotNullable().WithDefaultValue(524288000L)
                .WithColumn("uploaded_total_bytes").AsInt64().NotNullable().WithDefaultValue(0L)
                .WithColumn("allowed_mime_types_json").AsCustom("jsonb").NotNullable().WithDefaultValue("[]")
                .WithColumn("allowed_extensions_json").AsCustom("jsonb").NotNullable().WithDefaultValue("[]")
                .WithColumn("file_retention_days").AsInt32().Nullable()
                .WithColumn("notify_on_upload").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("is_revoked").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("revoked_at_utc").AsDateTimeOffset().Nullable()
                .WithColumn("revoked_by").AsString(255).Nullable()
                .WithColumn("created_by").AsString(255).Nullable()
                .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
        }

        if (!Schema.Table("managed_files").Exists())
        {
            Create.Table("managed_files")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("name").AsString(255).NotNullable()
                .WithColumn("file_name").AsString(255).NotNullable()
                .WithColumn("description").AsString(4000).Nullable()
                .WithColumn("category").AsString(100).Nullable()
                .WithColumn("folder_path").AsString(500).NotNullable().WithDefaultValue("/")
                .WithColumn("tags_json").AsCustom("jsonb").NotNullable().WithDefaultValue("[]")
                .WithColumn("scope").AsInt16().NotNullable().WithDefaultValue(0)
                .WithColumn("client_id").AsGuid().Nullable().ForeignKey("fk_managed_files_client", "clients", "id").OnDelete(System.Data.Rule.SetNull)
                .WithColumn("site_id").AsGuid().Nullable().ForeignKey("fk_managed_files_site", "sites", "id").OnDelete(System.Data.Rule.SetNull)
                .WithColumn("visibility").AsInt16().NotNullable().WithDefaultValue(0)
                .WithColumn("source").AsInt16().NotNullable().WithDefaultValue(0)
                .WithColumn("source_upload_link_id").AsGuid().Nullable().ForeignKey("fk_managed_files_upload_link", "file_upload_links", "id").OnDelete(System.Data.Rule.SetNull)
                .WithColumn("storage_object_key").AsString(1024).NotNullable().Unique("uq_managed_files_object_key")
                .WithColumn("storage_bucket").AsString(255).NotNullable()
                .WithColumn("content_type").AsString(255).NotNullable()
                .WithColumn("size_bytes").AsInt64().NotNullable()
                .WithColumn("sha256").AsString(64).Nullable()
                .WithColumn("download_count").AsInt64().NotNullable().WithDefaultValue(0L)
                .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("expires_at_utc").AsDateTimeOffset().Nullable()
                .WithColumn("created_by").AsString(255).Nullable()
                .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
                .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
                .WithColumn("deleted_at").AsDateTimeOffset().Nullable();

            Create.Index("ix_managed_files_visibility_active").OnTable("managed_files")
                .OnColumn("visibility").Ascending()
                .OnColumn("is_active").Ascending()
                .OnColumn("deleted_at").Ascending();

            Create.Index("ix_managed_files_scope_tenant").OnTable("managed_files")
                .OnColumn("scope").Ascending()
                .OnColumn("client_id").Ascending()
                .OnColumn("site_id").Ascending()
                .OnColumn("is_active").Ascending();

            Create.Index("ix_managed_files_category").OnTable("managed_files")
                .OnColumn("category").Ascending();
        }

        if (!Schema.Table("file_share_links").Exists())
        {
            Create.Table("file_share_links")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("short_code").AsString(32).NotNullable().Unique("uq_file_share_links_short_code")
                .WithColumn("title").AsString(255).NotNullable()
                .WithColumn("message").AsString(4000).Nullable()
                .WithColumn("client_id").AsGuid().Nullable().ForeignKey("fk_file_share_links_client", "clients", "id").OnDelete(System.Data.Rule.SetNull)
                .WithColumn("site_id").AsGuid().Nullable().ForeignKey("fk_file_share_links_site", "sites", "id").OnDelete(System.Data.Rule.SetNull)
                .WithColumn("require_passcode").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("passcode_hash").AsString(512).Nullable()
                .WithColumn("failed_passcode_attempts").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("locked_until_utc").AsDateTimeOffset().Nullable()
                .WithColumn("expires_at_utc").AsDateTimeOffset().NotNullable()
                .WithColumn("max_downloads").AsInt32().Nullable()
                .WithColumn("download_count").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("is_revoked").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("revoked_at_utc").AsDateTimeOffset().Nullable()
                .WithColumn("revoked_by").AsString(255).Nullable()
                .WithColumn("created_by").AsString(255).Nullable()
                .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
        }

        if (!Schema.Table("file_share_link_items").Exists())
        {
            Create.Table("file_share_link_items")
                .WithColumn("share_link_id").AsGuid().NotNullable().ForeignKey("fk_file_share_items_link", "file_share_links", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("managed_file_id").AsGuid().NotNullable().ForeignKey("fk_file_share_items_file", "managed_files", "id").OnDelete(System.Data.Rule.Cascade);

            Create.PrimaryKey("pk_file_share_link_items")
                .OnTable("file_share_link_items")
                .Columns("share_link_id", "managed_file_id");
        }

        if (!Schema.Table("file_access_logs").Exists())
        {
            Create.Table("file_access_logs")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("event_type").AsString(50).NotNullable()
                .WithColumn("managed_file_id").AsGuid().Nullable()
                .WithColumn("share_link_id").AsGuid().Nullable()
                .WithColumn("upload_link_id").AsGuid().Nullable()
                .WithColumn("agent_id").AsGuid().Nullable()
                .WithColumn("client_ip").AsString(64).Nullable()
                .WithColumn("user_agent").AsString(512).Nullable()
                .WithColumn("details_json").AsCustom("jsonb").Nullable()
                .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

            Create.Index("ix_file_access_logs_created").OnTable("file_access_logs")
                .OnColumn("created_at").Descending();
        }

        // Configurações globais + herdáveis (Global → Client → Site)
        if (!Schema.Table("server_configurations").Column("public_download_page_enabled").Exists())
        {
            Alter.Table("server_configurations")
                .AddColumn("public_download_page_enabled").AsBoolean().NotNullable().WithDefaultValue(false)
                .AddColumn("public_download_zero_touch_enabled").AsBoolean().NotNullable().WithDefaultValue(false)
                .AddColumn("public_download_title").AsString(200).Nullable()
                .AddColumn("public_download_description").AsString(2000).Nullable()
                .AddColumn("files_tab_enabled").AsBoolean().NotNullable().WithDefaultValue(true);
        }

        if (!Schema.Table("client_configurations").Column("files_tab_enabled").Exists())
        {
            Alter.Table("client_configurations")
                .AddColumn("files_tab_enabled").AsBoolean().Nullable();
        }

        if (!Schema.Table("site_configurations").Column("files_tab_enabled").Exists())
        {
            Alter.Table("site_configurations")
                .AddColumn("files_tab_enabled").AsBoolean().Nullable();
        }

        // Seed RBAC ResourceType.Files
        var actions = new[] { "View", "Create", "Edit", "Delete", "Execute" };
        foreach (var action in actions)
        {
            Execute.Sql($@"
                INSERT INTO permissions (id, resource_type, action_type, description)
                VALUES (gen_random_uuid(), 'Files', '{action}', 'Files:{action}')
                ON CONFLICT (resource_type, action_type) DO NOTHING;
            ");
        }

        Execute.Sql(@"
            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON p.resource_type = 'Files'
            WHERE r.name IN ('Admin', 'Manager', 'Operator')
              AND r.is_system = true
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
            WHERE rp.permission_id = p.id AND p.resource_type = 'Files';
        ");
        Execute.Sql(@"
            DELETE FROM permissions p
            WHERE p.resource_type = 'Files'
              AND NOT EXISTS (SELECT 1 FROM role_permissions rp WHERE rp.permission_id = p.id);
        ");

        if (Schema.Table("site_configurations").Column("files_tab_enabled").Exists())
            Delete.Column("files_tab_enabled").FromTable("site_configurations");
        if (Schema.Table("client_configurations").Column("files_tab_enabled").Exists())
            Delete.Column("files_tab_enabled").FromTable("client_configurations");

        if (Schema.Table("server_configurations").Column("files_tab_enabled").Exists())
        {
            Delete.Column("files_tab_enabled").FromTable("server_configurations");
            Delete.Column("public_download_description").FromTable("server_configurations");
            Delete.Column("public_download_title").FromTable("server_configurations");
            Delete.Column("public_download_zero_touch_enabled").FromTable("server_configurations");
            Delete.Column("public_download_page_enabled").FromTable("server_configurations");
        }

        if (Schema.Table("file_access_logs").Exists()) Delete.Table("file_access_logs");
        if (Schema.Table("file_share_link_items").Exists()) Delete.Table("file_share_link_items");
        if (Schema.Table("file_share_links").Exists()) Delete.Table("file_share_links");
        if (Schema.Table("managed_files").Exists()) Delete.Table("managed_files");
        if (Schema.Table("file_upload_links").Exists()) Delete.Table("file_upload_links");
    }
}
