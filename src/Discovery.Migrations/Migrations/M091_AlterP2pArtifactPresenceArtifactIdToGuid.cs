using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Altera artifact_id de varchar(512) para uuid na tabela p2p_artifact_presence.
/// Vincula artifacts aos packages do AppPackage/Winget/Chocolatey.
/// Dados existentes são descartados (TTL 2h, efêmeros por natureza).
/// </summary>
[Migration(20260513_091)]
public class M091_AlterP2pArtifactPresenceArtifactIdToGuid : Migration
{
    public override void Up()
    {
        // Remove índices que referenciam artifact_id
        Delete.Index("ix_p2p_presence_artifact_time").OnTable("p2p_artifact_presence");

        // Remove PK composta que inclui artifact_id
        Delete.PrimaryKey("pk_p2p_artifact_presence").FromTable("p2p_artifact_presence");

        // Trunca dados da janela atual (efêmeros, TTL 2h)
        Delete.FromTable("p2p_artifact_presence").AllRows();

        // Altera coluna de varchar para uuid
        Alter.Table("p2p_artifact_presence")
            .AlterColumn("artifact_id").AsGuid().NotNullable();

        // Recria PK composta
        Create.PrimaryKey("pk_p2p_artifact_presence")
            .OnTable("p2p_artifact_presence")
            .Columns("artifact_id", "agent_id");

        // Recria índice artifact_id + last_seen_at
        Create.Index("ix_p2p_presence_artifact_time")
            .OnTable("p2p_artifact_presence")
            .OnColumn("artifact_id").Ascending()
            .OnColumn("last_seen_at").Descending();
    }

    public override void Down()
    {
        Delete.Index("ix_p2p_presence_artifact_time").OnTable("p2p_artifact_presence");
        Delete.PrimaryKey("pk_p2p_artifact_presence").FromTable("p2p_artifact_presence");
        Delete.FromTable("p2p_artifact_presence").AllRows();

        Alter.Table("p2p_artifact_presence")
            .AlterColumn("artifact_id").AsString(512).NotNullable();

        Create.PrimaryKey("pk_p2p_artifact_presence")
            .OnTable("p2p_artifact_presence")
            .Columns("artifact_id", "agent_id");

        Create.Index("ix_p2p_presence_artifact_time")
            .OnTable("p2p_artifact_presence")
            .OnColumn("artifact_id").Ascending()
            .OnColumn("last_seen_at").Descending();
    }
}
