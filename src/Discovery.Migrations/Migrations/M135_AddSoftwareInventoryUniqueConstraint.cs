using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260717_135)]
public class M135_AddSoftwareInventoryUniqueConstraint : Migration
{
    public override void Up()
    {
        // Remove duplicatas existentes antes de criar a unique constraint
        Execute.Sql(@"
            DELETE FROM agent_software_inventory
            WHERE id IN (
                SELECT id FROM (
                    SELECT id,
                           ROW_NUMBER() OVER (
                               PARTITION BY agent_id, software_id
                               ORDER BY updated_at DESC
                           ) AS rn
                    FROM agent_software_inventory
                ) dup
                WHERE dup.rn > 1
            );
        ");

        // Remove o índice simples existente
        if (Schema.Table("agent_software_inventory").Index("ix_agent_software_inventory_agent_software").Exists())
        {
            Delete.Index("ix_agent_software_inventory_agent_software")
                .OnTable("agent_software_inventory");
        }

        // Cria índice único composto
        Create.Index("ix_agent_software_inventory_agent_software_unique")
            .OnTable("agent_software_inventory")
            .OnColumn("agent_id").Ascending()
            .OnColumn("software_id").Ascending()
            .WithOptions().Unique();
    }

    public override void Down()
    {
        if (Schema.Table("agent_software_inventory").Index("ix_agent_software_inventory_agent_software_unique").Exists())
        {
            Delete.Index("ix_agent_software_inventory_agent_software_unique")
                .OnTable("agent_software_inventory");
        }

        if (!Schema.Table("agent_software_inventory").Index("ix_agent_software_inventory_agent_software").Exists())
        {
            Create.Index("ix_agent_software_inventory_agent_software")
                .OnTable("agent_software_inventory")
                .OnColumn("agent_id").Ascending()
                .OnColumn("software_id").Ascending();
        }
    }
}
