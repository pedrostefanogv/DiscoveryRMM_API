using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Política de WebRTC do acesso remoto (herdável servidor → cliente → site) e
/// seu kill switch booleano.
///
/// Defaults preservam o comportamento ANTERIOR:
///   - servidor:  policy = '{}'  e enabled = false  ⇒ todo o tráfego pelo NATS;
///   - cliente/site: NULL ⇒ herdam do nível acima.
///
/// A coluna booleana é separada do blob de propósito: permite desligar o WebRTC
/// em um site sem apagar a configuração de STUN/TURN que ele já tinha, e reusa o
/// formato de remote_access_enabled (a UI de locks/herança já sabe exibi-lo).
/// </summary>
[Migration(20270123_290)]
public class M290_AddRemoteAccessWebRtcPolicy : Migration
{
    public override void Up()
    {
        if (!Schema.Table("server_configurations").Column("remote_access_webrtc_policy_json").Exists())
        {
            Alter.Table("server_configurations")
                .AddColumn("remote_access_webrtc_policy_json").AsString().NotNullable().WithDefaultValue("{}");
        }

        if (!Schema.Table("server_configurations").Column("remote_access_webrtc_enabled").Exists())
        {
            Alter.Table("server_configurations")
                .AddColumn("remote_access_webrtc_enabled").AsBoolean().NotNullable().WithDefaultValue(false);
        }

        if (!Schema.Table("client_configurations").Column("remote_access_webrtc_policy_json").Exists())
        {
            Alter.Table("client_configurations")
                .AddColumn("remote_access_webrtc_policy_json").AsString().Nullable();
        }

        if (!Schema.Table("client_configurations").Column("remote_access_webrtc_enabled").Exists())
        {
            Alter.Table("client_configurations")
                .AddColumn("remote_access_webrtc_enabled").AsBoolean().Nullable();
        }

        if (!Schema.Table("site_configurations").Column("remote_access_webrtc_policy_json").Exists())
        {
            Alter.Table("site_configurations")
                .AddColumn("remote_access_webrtc_policy_json").AsString().Nullable();
        }

        if (!Schema.Table("site_configurations").Column("remote_access_webrtc_enabled").Exists())
        {
            Alter.Table("site_configurations")
                .AddColumn("remote_access_webrtc_enabled").AsBoolean().Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("site_configurations").Column("remote_access_webrtc_enabled").Exists())
        {
            Delete.Column("remote_access_webrtc_enabled").FromTable("site_configurations");
        }

        if (Schema.Table("site_configurations").Column("remote_access_webrtc_policy_json").Exists())
        {
            Delete.Column("remote_access_webrtc_policy_json").FromTable("site_configurations");
        }

        if (Schema.Table("client_configurations").Column("remote_access_webrtc_enabled").Exists())
        {
            Delete.Column("remote_access_webrtc_enabled").FromTable("client_configurations");
        }

        if (Schema.Table("client_configurations").Column("remote_access_webrtc_policy_json").Exists())
        {
            Delete.Column("remote_access_webrtc_policy_json").FromTable("client_configurations");
        }

        if (Schema.Table("server_configurations").Column("remote_access_webrtc_enabled").Exists())
        {
            Delete.Column("remote_access_webrtc_enabled").FromTable("server_configurations");
        }

        if (Schema.Table("server_configurations").Column("remote_access_webrtc_policy_json").Exists())
        {
            Delete.Column("remote_access_webrtc_policy_json").FromTable("server_configurations");
        }
    }
}
