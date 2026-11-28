using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona <c>mtls_cert_serial</c> e <c>mtls_cert_expires_at_utc</c> em <c>agents</c>
/// para rastrear a cobertura de enrollment mTLS P2P na frota.
/// </summary>
[Migration(20270129_306)]
public class M306_AddAgentMtlsCertMetadata : Migration
{
    public override void Up()
    {
        if (Schema.Table("agents").Exists())
        {
            if (!Schema.Table("agents").Column("mtls_cert_serial").Exists())
            {
                Alter.Table("agents")
                    .AddColumn("mtls_cert_serial").AsString(128).Nullable();
            }

            if (!Schema.Table("agents").Column("mtls_cert_expires_at_utc").Exists())
            {
                Alter.Table("agents")
                    .AddColumn("mtls_cert_expires_at_utc").AsDateTimeOffset().Nullable();
            }
        }
    }

    public override void Down()
    {
        if (Schema.Table("agents").Exists())
        {
            if (Schema.Table("agents").Column("mtls_cert_expires_at_utc").Exists())
                Delete.Column("mtls_cert_expires_at_utc").FromTable("agents");
            if (Schema.Table("agents").Column("mtls_cert_serial").Exists())
                Delete.Column("mtls_cert_serial").FromTable("agents");
        }
    }
}
