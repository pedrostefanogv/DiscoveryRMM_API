using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260301_001)]
public class M001_CreateClients : Migration
{
    public override void Up()
    {
        Create.Table("clients")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("document").AsString(50).Nullable()
            .WithColumn("email").AsString(200).Nullable()
            .WithColumn("phone").AsString(50).Nullable()
            .WithColumn("notes").AsString(2000).Nullable()
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
    }

    public override void Down()
    {
        Delete.Table("clients");
    }
}
