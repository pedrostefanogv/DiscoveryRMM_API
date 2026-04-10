using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260311_056)]
public class M056_AddFeaturePoliciesConfigurationFields : Migration
{
    public override void Up()
    {
        if (!Schema.Table("server_configurations").Column("chat_ai_enabled").Exists())
        {
            Alter.Table("server_configurations")
                .AddColumn("chat_ai_enabled").AsBoolean().NotNullable().WithDefaultValue(false);
        }

        if (!Schema.Table("client_configurations").Column("chat_ai_enabled").Exists())
        {
            Alter.Table("client_configurations")
                .AddColumn("chat_ai_enabled").AsBoolean().Nullable();
        }

        if (!Schema.Table("client_configurations").Column("knowledge_base_enabled").Exists())
        {
            Alter.Table("client_configurations")
                .AddColumn("knowledge_base_enabled").AsBoolean().Nullable();
        }

        if (!Schema.Table("site_configurations").Column("chat_ai_enabled").Exists())
        {
            Alter.Table("site_configurations")
                .AddColumn("chat_ai_enabled").AsBoolean().Nullable();
        }

        if (!Schema.Table("site_configurations").Column("knowledge_base_enabled").Exists())
        {
            Alter.Table("site_configurations")
                .AddColumn("knowledge_base_enabled").AsBoolean().Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("site_configurations").Column("knowledge_base_enabled").Exists())
        {
            Delete.Column("knowledge_base_enabled").FromTable("site_configurations");
        }

        if (Schema.Table("site_configurations").Column("chat_ai_enabled").Exists())
        {
            Delete.Column("chat_ai_enabled").FromTable("site_configurations");
        }

        if (Schema.Table("client_configurations").Column("knowledge_base_enabled").Exists())
        {
            Delete.Column("knowledge_base_enabled").FromTable("client_configurations");
        }

        if (Schema.Table("client_configurations").Column("chat_ai_enabled").Exists())
        {
            Delete.Column("chat_ai_enabled").FromTable("client_configurations");
        }

        if (Schema.Table("server_configurations").Column("chat_ai_enabled").Exists())
        {
            Delete.Column("chat_ai_enabled").FromTable("server_configurations");
        }
    }
}
