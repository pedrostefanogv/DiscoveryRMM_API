using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Policy-sync de automação: guarda o fingerprint entregue ao agent e quando.
///
/// Antes, a aba "Políticas" só mostrava o fingerprint ATUAL calculado pelo
/// servidor — não havia como saber se o agent já tinha recebido aquela policy.
/// Com last_policy_fingerprint o preview responde "agent atualizado/
/// desatualizado" comparando os dois valores; last_policy_sync_at diz quando a
/// policy vigente foi entregue.
/// </summary>
[Migration(20261123_204)]
public class M204_AgentPolicySyncFingerprint : Migration
{
    public override void Up()
    {
        if (!Schema.Table("agents").Column("last_policy_fingerprint").Exists())
        {
            Alter.Table("agents")
                .AddColumn("last_policy_fingerprint").AsString(64).Nullable();
        }

        if (!Schema.Table("agents").Column("last_policy_sync_at").Exists())
        {
            Alter.Table("agents")
                .AddColumn("last_policy_sync_at").AsCustom("timestamptz").Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("agents").Column("last_policy_sync_at").Exists())
            Delete.Column("last_policy_sync_at").FromTable("agents");

        if (Schema.Table("agents").Column("last_policy_fingerprint").Exists())
            Delete.Column("last_policy_fingerprint").FromTable("agents");
    }
}
