using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260417_103)]
public class M103_AddKbLinkFeedback : Migration
{
    public override void Up()
    {
        Alter.Table("ticket_knowledge_links")
            .AddColumn("feedback_useful").AsBoolean().Nullable()
            .AddColumn("feedback_at").AsDateTimeOffset().Nullable();
    }

    public override void Down()
    {
        Delete.Column("feedback_useful").FromTable("ticket_knowledge_links");
        Delete.Column("feedback_at").FromTable("ticket_knowledge_links");
    }
}
