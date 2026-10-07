using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Kill-switch de zero-touch provisioning herdável (servidor → cliente → site).
///
/// Quando desativado, agents sem credenciais não iniciam o registro zero-touch
/// via P2P onboarding. Default true no servidor para preservar o comportamento
/// atual; cliente/site ficam nulos (herdam) até que alguém sobrescreva.
/// </summary>
[Migration(20261030_197)]
public class M197_AddZeroTouchEnabled : Migration
{
    public override void Up()
    {
        if (Schema.Table("server_configurations").Exists()
            && !Schema.Table("server_configurations").Column("zero_touch_enabled").Exists())
        {
            Alter.Table("server_configurations")
                .AddColumn("zero_touch_enabled").AsBoolean().NotNullable().WithDefaultValue(true);
        }

        if (Schema.Table("client_configurations").Exists()
            && !Schema.Table("client_configurations").Column("zero_touch_enabled").Exists())
        {
            Alter.Table("client_configurations")
                .AddColumn("zero_touch_enabled").AsBoolean().Nullable();
        }

        if (Schema.Table("site_configurations").Exists()
            && !Schema.Table("site_configurations").Column("zero_touch_enabled").Exists())
        {
            Alter.Table("site_configurations")
                .AddColumn("zero_touch_enabled").AsBoolean().Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("server_configurations").Exists()
            && Schema.Table("server_configurations").Column("zero_touch_enabled").Exists())
        {
            Delete.Column("zero_touch_enabled").FromTable("server_configurations");
        }

        if (Schema.Table("client_configurations").Exists()
            && Schema.Table("client_configurations").Column("zero_touch_enabled").Exists())
        {
            Delete.Column("zero_touch_enabled").FromTable("client_configurations");
        }

        if (Schema.Table("site_configurations").Exists()
            && Schema.Table("site_configurations").Column("zero_touch_enabled").Exists())
        {
            Delete.Column("zero_touch_enabled").FromTable("site_configurations");
        }
    }
}
