using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Anexos em dois níveis: comentário (conversa, pequeno) e chamado (repositório,
/// arquivos grandes com upload resumível). Os limites/tipos ficam configuráveis
/// no console e são resolvidos por Site > Cliente > Servidor.
/// </summary>
[Migration(20260312_211)]
public class M211_AddAttachmentTierSettingsToServerConfiguration : Migration
{
    public override void Up()
    {
        if (!Schema.Table("server_configurations").Column("attachment_comment_max_file_size_bytes").Exists())
            Alter.Table("server_configurations").AddColumn("attachment_comment_max_file_size_bytes")
                .AsInt64().NotNullable().WithDefaultValue(10L * 1024 * 1024);

        if (!Schema.Table("server_configurations").Column("attachment_comment_allowed_content_types").Exists())
            Alter.Table("server_configurations").AddColumn("attachment_comment_allowed_content_types")
                .AsString(1000).NotNullable().WithDefaultValue("image/jpeg,image/png,image/webp,application/pdf");

        if (!Schema.Table("server_configurations").Column("attachment_ticket_max_file_size_bytes").Exists())
            Alter.Table("server_configurations").AddColumn("attachment_ticket_max_file_size_bytes")
                .AsInt64().NotNullable().WithDefaultValue(2L * 1024 * 1024 * 1024);

        if (!Schema.Table("server_configurations").Column("attachment_ticket_allowed_content_types").Exists())
            Alter.Table("server_configurations").AddColumn("attachment_ticket_allowed_content_types")
                .AsString(2000).NotNullable().WithDefaultValue("");

        if (!Schema.Table("server_configurations").Column("attachment_ticket_total_quota_bytes").Exists())
            Alter.Table("server_configurations").AddColumn("attachment_ticket_total_quota_bytes")
                .AsInt64().NotNullable().WithDefaultValue(0);
    }

    public override void Down()
    {
        foreach (var column in new[]
                 {
                     "attachment_comment_max_file_size_bytes", "attachment_comment_allowed_content_types",
                     "attachment_ticket_max_file_size_bytes", "attachment_ticket_allowed_content_types",
                     "attachment_ticket_total_quota_bytes",
                 })
        {
            if (Schema.Table("server_configurations").Column(column).Exists())
                Delete.Column(column).FromTable("server_configurations");
        }
    }
}
