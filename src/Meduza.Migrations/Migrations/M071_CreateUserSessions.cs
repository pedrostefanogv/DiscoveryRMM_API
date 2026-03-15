using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260316_071)]
public class M071_CreateUserSessions : Migration
{
    public override void Up()
    {
        Create.Table("user_sessions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
                .ForeignKey("fk_user_sessions_user", "users", "id")
            .WithColumn("access_token_hash").AsString(128).NotNullable()
            .WithColumn("refresh_token_hash").AsString(128).NotNullable().Unique("ix_user_sessions_refresh_token_hash")
            .WithColumn("mfa_verified").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("ip_address").AsString(64).Nullable()
            .WithColumn("user_agent").AsString(512).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("expires_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("revoked_at").AsCustom("timestamptz").Nullable();

        Create.Index("ix_user_sessions_user_active")
            .OnTable("user_sessions")
            .OnColumn("user_id").Ascending()
            .OnColumn("revoked_at").Ascending();
    }

    public override void Down()
    {
        Delete.Table("user_sessions");
    }
}
