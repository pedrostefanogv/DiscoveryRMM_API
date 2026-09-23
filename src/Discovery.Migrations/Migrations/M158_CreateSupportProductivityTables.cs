using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Tabelas de produtividade de suporte: macros (respostas rápidas), templates de
/// chamado, membros de departamento (round-robin) e canais de notificação
/// multicanal. Também adiciona a estratégia de auto-atribuição ao departamento.
/// </summary>
[Migration(20260923_158)]
public class M158_CreateSupportProductivityTables : Migration
{
    public override void Up()
    {
        if (!Schema.Table("ticket_macros").Exists())
        {
            Create.Table("ticket_macros")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("client_id").AsGuid().Nullable()
                .WithColumn("department_id").AsGuid().Nullable()
                .WithColumn("name").AsString(200).NotNullable()
                .WithColumn("description").AsString(1000).Nullable()
                .WithColumn("content").AsCustom("text").NotNullable()
                .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("created_by").AsString(255).Nullable()
                .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
                .WithColumn("updated_at").AsCustom("timestamptz").NotNullable();

            Create.Index("ix_ticket_macros_client").OnTable("ticket_macros").OnColumn("client_id").Ascending();
            Create.Index("ix_ticket_macros_department").OnTable("ticket_macros").OnColumn("department_id").Ascending();
        }

        if (!Schema.Table("ticket_templates").Exists())
        {
            Create.Table("ticket_templates")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("client_id").AsGuid().Nullable()
                .WithColumn("department_id").AsGuid().Nullable()
                .WithColumn("name").AsString(200).NotNullable()
                .WithColumn("title").AsString(500).NotNullable()
                .WithColumn("description").AsCustom("text").NotNullable()
                .WithColumn("priority").AsString(50).Nullable()
                .WithColumn("category").AsString(100).Nullable()
                .WithColumn("custom_field_defaults_json").AsCustom("jsonb").NotNullable().WithDefaultValue("{}")
                .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("created_by").AsString(255).Nullable()
                .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
                .WithColumn("updated_at").AsCustom("timestamptz").NotNullable();

            Create.Index("ix_ticket_templates_client").OnTable("ticket_templates").OnColumn("client_id").Ascending();
            Create.Index("ix_ticket_templates_department").OnTable("ticket_templates").OnColumn("department_id").Ascending();
        }

        if (!Schema.Table("department_members").Exists())
        {
            Create.Table("department_members")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("department_id").AsGuid().NotNullable()
                .WithColumn("user_id").AsGuid().NotNullable()
                .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("created_at").AsCustom("timestamptz").NotNullable();

            Create.ForeignKey("fk_department_members_department")
                .FromTable("department_members").ForeignColumn("department_id")
                .ToTable("departments").PrimaryColumn("id")
                .OnDelete(System.Data.Rule.Cascade);

            Create.Index("ux_department_members_dept_user")
                .OnTable("department_members")
                .OnColumn("department_id").Ascending()
                .OnColumn("user_id").Ascending()
                .WithOptions().Unique();
        }

        if (!Schema.Table("notification_channels").Exists())
        {
            Create.Table("notification_channels")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("name").AsString(200).NotNullable()
                .WithColumn("type").AsString(30).NotNullable()
                .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("events_json").AsCustom("jsonb").NotNullable().WithDefaultValue("[]")
                .WithColumn("config_json").AsCustom("jsonb").NotNullable().WithDefaultValue("{}")
                .WithColumn("created_by").AsString(255).Nullable()
                .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
                .WithColumn("updated_at").AsCustom("timestamptz").NotNullable();

            Create.Index("ix_notification_channels_active").OnTable("notification_channels").OnColumn("is_active").Ascending();
        }

        if (Schema.Table("departments").Exists())
        {
            if (!Schema.Table("departments").Column("assignment_strategy").Exists())
                Alter.Table("departments").AddColumn("assignment_strategy").AsInt32().NotNullable().WithDefaultValue(0);
            if (!Schema.Table("departments").Column("round_robin_last_user_id").Exists())
                Alter.Table("departments").AddColumn("round_robin_last_user_id").AsGuid().Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("departments").Exists())
        {
            if (Schema.Table("departments").Column("round_robin_last_user_id").Exists())
                Delete.Column("round_robin_last_user_id").FromTable("departments");
            if (Schema.Table("departments").Column("assignment_strategy").Exists())
                Delete.Column("assignment_strategy").FromTable("departments");
        }
        if (Schema.Table("ticket_macros").Exists()) Delete.Table("ticket_macros");
        if (Schema.Table("ticket_templates").Exists()) Delete.Table("ticket_templates");
        if (Schema.Table("department_members").Exists()) Delete.Table("department_members");
        if (Schema.Table("notification_channels").Exists()) Delete.Table("notification_channels");
    }
}
