using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260316_070)]
public class M070_CreateUserMfaKeys : Migration
{
    public override void Up()
    {
        Create.Table("user_mfa_keys")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
                .ForeignKey("fk_user_mfa_keys_user", "users", "id")
            .WithColumn("key_type").AsString(32).NotNullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("credential_id_base64").AsString(512).Nullable()
            .WithColumn("public_key_base64").AsString(2048).Nullable()
            .WithColumn("sign_count").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("aaguid_base64").AsString(64).Nullable()
            .WithColumn("user_handle_base64").AsString(128).Nullable()
            .WithColumn("otp_secret_encrypted").AsString(512).Nullable()
            .WithColumn("backup_code_hashes").AsCustom("text[]").Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("last_used_at").AsCustom("timestamptz").Nullable();

        Create.Index("ix_user_mfa_keys_user_id")
            .OnTable("user_mfa_keys")
            .OnColumn("user_id").Ascending();
    }

    public override void Down()
    {
        Delete.Table("user_mfa_keys");
    }
}

[Migration(20260324_070)]
public class M070_AddNatsXKeySeed : Migration
{
    public override void Up()
    {
        Alter.Table("server_configurations")
            .AddColumn("nats_xkey_seed").AsCustom("text").NotNullable().WithDefaultValue(string.Empty);
    }

    public override void Down()
    {
        Delete.Column("nats_xkey_seed").FromTable("server_configurations");
    }
}
