using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260304_021)]
public class M021_CreateEntityNotes : Migration
{
    public override void Up()
    {
        Create.Table("entity_notes")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().Nullable().ForeignKey("fk_entity_notes_client", "clients", "id")
            .WithColumn("site_id").AsGuid().Nullable().ForeignKey("fk_entity_notes_site", "sites", "id")
            .WithColumn("agent_id").AsGuid().Nullable().ForeignKey("fk_entity_notes_agent", "agents", "id")
            .WithColumn("content").AsString(4000).NotNullable()
            .WithColumn("author").AsString(200).Nullable()
            .WithColumn("is_pinned").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_entity_notes_client_created_at")
            .OnTable("entity_notes")
            .OnColumn("client_id").Ascending()
            .OnColumn("created_at").Descending();

        Create.Index("ix_entity_notes_site_created_at")
            .OnTable("entity_notes")
            .OnColumn("site_id").Ascending()
            .OnColumn("created_at").Descending();

        Create.Index("ix_entity_notes_agent_created_at")
            .OnTable("entity_notes")
            .OnColumn("agent_id").Ascending()
            .OnColumn("created_at").Descending();

        // Garante que somente um alvo esteja preenchido por linha.
        IfDatabase("postgres").Execute.Sql(@"
            ALTER TABLE entity_notes
            ADD CONSTRAINT ck_entity_notes_single_target
            CHECK (
                (CASE WHEN client_id IS NOT NULL THEN 1 ELSE 0 END) +
                (CASE WHEN site_id IS NOT NULL THEN 1 ELSE 0 END) +
                (CASE WHEN agent_id IS NOT NULL THEN 1 ELSE 0 END) = 1
            );
        ");
    }

    public override void Down()
    {
        Delete.Table("entity_notes");
    }
}
