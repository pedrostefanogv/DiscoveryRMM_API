using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Cria a tabela agent_p2p_bootstraps para o sistema de bootstrap P2P via cloud.
/// Cada linha representa o último registro de um agent ativo no sistema P2P.
/// Upsert por agent_id; índice em client_id para busca eficiente de peers do mesmo cliente.
/// </summary>
[Migration(20260326_090)]
public class M090_CreateAgentP2pBootstrap : Migration
{
    public override void Up()
    {
        Create.Table("agent_p2p_bootstraps")
            .WithColumn("agent_id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().NotNullable()
            .WithColumn("peer_id").AsString(128).NotNullable()
            .WithColumn("addrs_json").AsString(1024).NotNullable().WithDefaultValue("[]")
            .WithColumn("port").AsInt32().NotNullable()
            .WithColumn("last_heartbeat_at").AsCustom("timestamptz").NotNullable()
                .WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_agent_p2p_bootstraps_client_id")
            .OnTable("agent_p2p_bootstraps")
            .OnColumn("client_id").Ascending();
    }

    public override void Down()
    {
        Delete.Table("agent_p2p_bootstraps");
    }
}
