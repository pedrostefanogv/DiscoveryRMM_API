using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Drop da tabela agent_p2p_bootstraps — substituída pelo heartbeat enriquecido
/// e pelo subject tenant.{clientId}.p2p.events (peer.online).
///
/// Dados de PeerId, Addrs e Port agora são transmitidos via NATS heartbeat e
/// cacheados no Redis (HeartbeatCacheService). A tabela não é mais populada.
/// </summary>
[Migration(20260513_127)]
public class M127_DropAgentP2pBootstraps : Migration
{
    public override void Up()
    {
        if (Schema.Table("agent_p2p_bootstraps").Exists())
        {
            Delete.Index("ix_agent_p2p_bootstraps_client_id").OnTable("agent_p2p_bootstraps");
            Delete.Table("agent_p2p_bootstraps");
        }
    }

    public override void Down()
    {
        if (!Schema.Table("agent_p2p_bootstraps").Exists())
        {
            Create.Table("agent_p2p_bootstraps")
                .WithColumn("agent_id").AsGuid().PrimaryKey()
                .WithColumn("client_id").AsGuid().NotNullable()
                .WithColumn("peer_id").AsString(128).NotNullable().WithDefaultValue(string.Empty)
                .WithColumn("addrs_json").AsString(1024).NotNullable().WithDefaultValue("[]")
                .WithColumn("port").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("last_heartbeat_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

            Create.Index("ix_agent_p2p_bootstraps_client_id")
                .OnTable("agent_p2p_bootstraps")
                .OnColumn("client_id").Ascending();
        }
    }
}
