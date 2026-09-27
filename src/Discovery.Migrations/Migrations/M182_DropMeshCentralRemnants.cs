using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Remove os resquícios de MeshCentral que ficaram fora da M139:
/// colunas de sincronização em \`users\` e máscara/perfil de rights em \`roles\`.
/// A integração foi descontinuada; estas colunas não são mais lidas/gravadas.
/// </summary>
[Migration(20261011_182)]
public class M182_DropMeshCentralRemnants : Migration
{
    private static readonly string[] UserColumns =
    [
        "meshcentral_user_id",
        "meshcentral_username",
        "meshcentral_last_synced_at",
        "meshcentral_sync_status",
        "meshcentral_sync_error"
    ];

    private static readonly string[] RoleColumns =
    [
        "mesh_rights_mask",
        "mesh_rights_profile"
    ];

    public override void Up()
    {
        if (Schema.Table("users").Index("ix_users_meshcentral_user_id").Exists())
        {
            Delete.Index("ix_users_meshcentral_user_id").OnTable("users");
        }

        foreach (var column in UserColumns)
        {
            if (Schema.Table("users").Column(column).Exists())
            {
                Delete.Column(column).FromTable("users");
            }
        }

        foreach (var column in RoleColumns)
        {
            if (Schema.Table("roles").Column(column).Exists())
            {
                Delete.Column(column).FromTable("roles");
            }
        }
    }

    public override void Down()
    {
        // MeshCentral foi removido intencionalmente — sem rollback.
    }
}
