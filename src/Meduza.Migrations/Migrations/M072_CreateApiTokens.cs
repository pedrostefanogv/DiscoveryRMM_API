using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260316_072)]
public class M072_CreateApiTokens : Migration
{
    public override void Up()
    {
        Create.Table("api_tokens")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
                .ForeignKey("fk_api_tokens_user", "users", "id")
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("token_id_public").AsString(50).NotNullable().Unique("ix_api_tokens_token_id_public")
            .WithColumn("access_key_hash").AsString(128).NotNullable()
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("last_used_at").AsCustom("timestamptz").Nullable()
            .WithColumn("expires_at").AsCustom("timestamptz").Nullable();

        Create.Index("ix_api_tokens_user_id")
            .OnTable("api_tokens")
            .OnColumn("user_id").Ascending();
    }

    public override void Down()
    {
        Delete.Table("api_tokens");
    }
}
