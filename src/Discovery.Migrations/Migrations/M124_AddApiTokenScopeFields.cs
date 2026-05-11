using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260511_124)]
public class M124_AddApiTokenScopeFields : Migration
{
    public override void Up()
    {
        Alter.Table("api_tokens")
            .AddColumn("scope_level").AsString(32).Nullable();

        Alter.Table("api_tokens")
            .AddColumn("scope_id").AsGuid().Nullable();
    }

    public override void Down()
    {
        Delete.Column("scope_level").FromTable("api_tokens");
        Delete.Column("scope_id").FromTable("api_tokens");
    }
}
