using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Cria a tabela <c>managed_file_folders</c> para organização hierárquica por pastas
/// na Central de Arquivos e no Portal Público (/download), além de índice em <c>managed_files.folder_path</c>.
/// </summary>
[Migration(20270129_304)]
public class M304_CreateManagedFileFolders : Migration
{
    public override void Up()
    {
        if (!Schema.Table("managed_file_folders").Exists())
        {
            Create.Table("managed_file_folders")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("name").AsString(120).NotNullable()
                .WithColumn("path").AsString(500).NotNullable()
                .WithColumn("parent_path").AsString(500).NotNullable().WithDefaultValue("/")
                .WithColumn("description").AsString(1000).Nullable()
                .WithColumn("scope").AsInt16().NotNullable().WithDefaultValue(0)
                .WithColumn("client_id").AsGuid().Nullable().ForeignKey("fk_managed_file_folders_client", "clients", "id").OnDelete(System.Data.Rule.SetNull)
                .WithColumn("site_id").AsGuid().Nullable().ForeignKey("fk_managed_file_folders_site", "sites", "id").OnDelete(System.Data.Rule.SetNull)
                .WithColumn("visibility").AsInt16().NotNullable().WithDefaultValue(2)
                .WithColumn("created_by").AsString(255).Nullable()
                .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
                .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

            Create.Index("ix_managed_file_folders_parent").OnTable("managed_file_folders")
                .OnColumn("parent_path").Ascending();

            Create.Index("ix_managed_file_folders_visibility").OnTable("managed_file_folders")
                .OnColumn("visibility").Ascending();

            Execute.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS uq_managed_file_folders_scope_path
                ON managed_file_folders (
                    scope,
                    COALESCE(client_id, '00000000-0000-0000-0000-000000000000'::uuid),
                    COALESCE(site_id, '00000000-0000-0000-0000-000000000000'::uuid),
                    LOWER(path)
                );
            ");
        }

        if (Schema.Table("managed_files").Exists() && !Schema.Table("managed_files").Index("ix_managed_files_folder_path").Exists())
        {
            Create.Index("ix_managed_files_folder_path").OnTable("managed_files")
                .OnColumn("folder_path").Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table("managed_files").Exists() && Schema.Table("managed_files").Index("ix_managed_files_folder_path").Exists())
        {
            Delete.Index("ix_managed_files_folder_path").OnTable("managed_files");
        }

        if (Schema.Table("managed_file_folders").Exists())
        {
            Delete.Table("managed_file_folders");
        }
    }
}
