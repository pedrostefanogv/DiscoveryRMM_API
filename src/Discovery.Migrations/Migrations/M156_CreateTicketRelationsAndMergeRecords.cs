using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Cria as tabelas de relações entre chamados (ticket_relations) e de histórico de
/// merge (ticket_merge_records). As entidades já existiam no domínio, mas nunca
/// houve migration correspondente — qualquer uso dessas tabelas falharia.
/// </summary>
[Migration(20260923_156)]
public class M156_CreateTicketRelationsAndMergeRecords : Migration
{
    public override void Up()
    {
        if (!Schema.Table("ticket_relations").Exists())
        {
            Create.Table("ticket_relations")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("source_ticket_id").AsGuid().NotNullable()
                .WithColumn("target_ticket_id").AsGuid().NotNullable()
                .WithColumn("relation_type").AsInt32().NotNullable()
                .WithColumn("created_by").AsString(255).Nullable()
                .WithColumn("created_at").AsCustom("timestamptz").NotNullable();

            Create.ForeignKey("fk_ticket_relations_source")
                .FromTable("ticket_relations").ForeignColumn("source_ticket_id")
                .ToTable("tickets").PrimaryColumn("id")
                .OnDelete(System.Data.Rule.Cascade);

            Create.ForeignKey("fk_ticket_relations_target")
                .FromTable("ticket_relations").ForeignColumn("target_ticket_id")
                .ToTable("tickets").PrimaryColumn("id")
                .OnDelete(System.Data.Rule.Cascade);

            Create.Index("ix_ticket_relations_source")
                .OnTable("ticket_relations").OnColumn("source_ticket_id").Ascending();
            Create.Index("ix_ticket_relations_target")
                .OnTable("ticket_relations").OnColumn("target_ticket_id").Ascending();
            Create.Index("ux_ticket_relations_unique")
                .OnTable("ticket_relations")
                .OnColumn("source_ticket_id").Ascending()
                .OnColumn("target_ticket_id").Ascending()
                .OnColumn("relation_type").Ascending()
                .WithOptions().Unique();
        }

        if (!Schema.Table("ticket_merge_records").Exists())
        {
            Create.Table("ticket_merge_records")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("source_ticket_id").AsGuid().NotNullable()
                .WithColumn("target_ticket_id").AsGuid().NotNullable()
                .WithColumn("merged_by").AsString(255).Nullable()
                .WithColumn("reason").AsString(1000).Nullable()
                .WithColumn("merged_at").AsCustom("timestamptz").NotNullable();

            Create.ForeignKey("fk_ticket_merge_records_source")
                .FromTable("ticket_merge_records").ForeignColumn("source_ticket_id")
                .ToTable("tickets").PrimaryColumn("id")
                .OnDelete(System.Data.Rule.Cascade);

            Create.ForeignKey("fk_ticket_merge_records_target")
                .FromTable("ticket_merge_records").ForeignColumn("target_ticket_id")
                .ToTable("tickets").PrimaryColumn("id")
                .OnDelete(System.Data.Rule.Cascade);

            Create.Index("ix_ticket_merge_records_source")
                .OnTable("ticket_merge_records").OnColumn("source_ticket_id").Ascending();
            Create.Index("ix_ticket_merge_records_target")
                .OnTable("ticket_merge_records").OnColumn("target_ticket_id").Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table("ticket_relations").Exists())
            Delete.Table("ticket_relations");
        if (Schema.Table("ticket_merge_records").Exists())
            Delete.Table("ticket_merge_records");
    }
}
