using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260515_129)]
public class M129_AddDepartmentScopeToCustomFields : Migration
{
    public override void Up()
    {
        // Remove o índice único antigo (scope_type, name) para permitir mesmo nome em departamentos diferentes
        Delete.Index("ux_custom_field_definitions_scope_name").OnTable("custom_field_definitions");

        // Adiciona coluna department_id na tabela custom_field_definitions
        Alter.Table("custom_field_definitions")
            .AddColumn("department_id").AsGuid().Nullable()
            .ForeignKey("fk_custom_field_definitions_department", "departments", "id")
            .OnDelete(System.Data.Rule.Cascade);

        // Adiciona coluna is_internal para controle de visibilidade
        Alter.Table("custom_field_definitions")
            .AddColumn("is_internal").AsBoolean().NotNullable().WithDefaultValue(false);

        // Novo índice único: (scope_type, name) para escopos sem departamento (department_id IS NULL)
        // Para department scope (scope_type = 5), uniqueness é (department_id, name)
        Execute.Sql(@"
            CREATE UNIQUE INDEX ux_custom_field_definitions_scope_name
                ON custom_field_definitions (scope_type, name)
                WHERE department_id IS NULL;

            CREATE UNIQUE INDEX ux_custom_field_definitions_scope_dept_name
                ON custom_field_definitions (scope_type, department_id, name)
                WHERE department_id IS NOT NULL;
        ");

        // Índice para busca por departamento
        Create.Index("ix_custom_field_definitions_department_id")
            .OnTable("custom_field_definitions")
            .OnColumn("department_id").Ascending();

        // Índice combinado para busca de schema público de departamento
        Create.Index("ix_custom_field_definitions_dept_internal")
            .OnTable("custom_field_definitions")
            .OnColumn("department_id").Ascending()
            .OnColumn("is_internal").Ascending()
            .OnColumn("is_active").Ascending();
    }

    public override void Down()
    {
        // Remove os índices novos
        Delete.Index("ix_custom_field_definitions_dept_internal").OnTable("custom_field_definitions");
        Delete.Index("ix_custom_field_definitions_department_id").OnTable("custom_field_definitions");

        // Remove os partial unique indexes
        Execute.Sql("DROP INDEX IF EXISTS ux_custom_field_definitions_scope_dept_name");
        Execute.Sql("DROP INDEX IF EXISTS ux_custom_field_definitions_scope_name");

        Delete.Column("is_internal").FromTable("custom_field_definitions");
        Delete.ForeignKey("fk_custom_field_definitions_department").OnTable("custom_field_definitions");
        Delete.Column("department_id").FromTable("custom_field_definitions");

        // Recria o índice único original
        Create.Index("ux_custom_field_definitions_scope_name")
            .OnTable("custom_field_definitions")
            .OnColumn("scope_type").Ascending()
            .OnColumn("name").Ascending()
            .WithOptions().Unique();
    }
}
