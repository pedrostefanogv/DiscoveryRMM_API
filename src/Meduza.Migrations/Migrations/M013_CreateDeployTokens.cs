using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260302_013)]
public class M013_CreateDeployTokens : Migration
{
    public override void Up()
    {
        Create.Table("deploy_tokens")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("token_hash").AsString(128).NotNullable().Unique()
            .WithColumn("token_prefix").AsString(12).NotNullable()
            .WithColumn("description").AsString(500).Nullable()
            .WithColumn("expires_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("revoked_at").AsCustom("timestamptz").Nullable()
            .WithColumn("last_used_at").AsCustom("timestamptz").Nullable()
            .WithColumn("used_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("max_uses").AsInt32().Nullable();

        Create.Index("ix_deploy_tokens_expires_at").OnTable("deploy_tokens").OnColumn("expires_at");
    }

    public override void Down()
    {
        Delete.Table("deploy_tokens");
    }
}
