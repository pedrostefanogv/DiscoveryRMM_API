using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260805_144)]
public class M144_AddDeviceFingerprintToAgents : Migration
{
    public override void Up()
    {
        if (!Schema.Table("agents").Column("tpm_ek_hash").Exists())
        {
            Alter.Table("agents")
                .AddColumn("tpm_ek_hash").AsString(64).Nullable();
        }

        if (!Schema.Table("agents").Column("smbios_uuid").Exists())
        {
            Alter.Table("agents")
                .AddColumn("smbios_uuid").AsString(64).Nullable();
        }

        if (!Schema.Table("agents").Column("fingerprint_hash").Exists())
        {
            Alter.Table("agents")
                .AddColumn("fingerprint_hash").AsString(64).Nullable();
        }

        if (!Schema.Table("agents").Index("ix_agents_fingerprint_hash").Exists())
        {
            Create.Index("ix_agents_fingerprint_hash")
                .OnTable("agents")
                .OnColumn("fingerprint_hash");
        }
    }

    public override void Down()
    {
        if (Schema.Table("agents").Index("ix_agents_fingerprint_hash").Exists())
        {
            Delete.Index("ix_agents_fingerprint_hash").OnTable("agents");
        }

        if (Schema.Table("agents").Column("fingerprint_hash").Exists())
        {
            Delete.Column("fingerprint_hash").FromTable("agents");
        }

        if (Schema.Table("agents").Column("smbios_uuid").Exists())
        {
            Delete.Column("smbios_uuid").FromTable("agents");
        }

        if (Schema.Table("agents").Column("tpm_ek_hash").Exists())
        {
            Delete.Column("tpm_ek_hash").FromTable("agents");
        }
    }
}
