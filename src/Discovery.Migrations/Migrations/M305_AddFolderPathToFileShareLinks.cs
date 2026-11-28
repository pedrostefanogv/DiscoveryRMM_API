using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona <c>folder_path</c> e <c>folder_scope</c> em <c>file_share_links</c>
/// para permitir links de compartilhamento dinâmicos vinculados a uma pasta específica.
/// </summary>
[Migration(20270129_305)]
public class M305_AddFolderPathToFileShareLinks : Migration
{
    public override void Up()
    {
        if (Schema.Table("file_share_links").Exists())
        {
            if (!Schema.Table("file_share_links").Column("folder_path").Exists())
            {
                Alter.Table("file_share_links")
                    .AddColumn("folder_path").AsString(500).Nullable();
            }

            if (!Schema.Table("file_share_links").Column("folder_scope").Exists())
            {
                Alter.Table("file_share_links")
                    .AddColumn("folder_scope").AsInt16().Nullable();
            }
        }
    }

    public override void Down()
    {
        if (Schema.Table("file_share_links").Exists())
        {
            if (Schema.Table("file_share_links").Column("folder_scope").Exists())
                Delete.Column("folder_scope").FromTable("file_share_links");
            if (Schema.Table("file_share_links").Column("folder_path").Exists())
                Delete.Column("folder_path").FromTable("file_share_links");
        }
    }
}
