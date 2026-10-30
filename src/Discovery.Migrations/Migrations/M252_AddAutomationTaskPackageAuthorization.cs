using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona à automation_task_definitions o resultado da checagem de
/// autorização do pacote avaliada na criação/edição da tarefa:
/// package_authorized (pacote está na App Store efetiva do escopo) e
/// package_warning (motivo — pacote fora da loja ou inexistente no catálogo
/// Winget). A policy de automação SOBREPÕE a loja: a tarefa é aceita mesmo sem
/// autorização; as colunas registram o aviso para a UI.
/// </summary>
[Migration(20261029_252)]
public class M252_AddAutomationTaskPackageAuthorization : Migration
{
    public override void Up()
    {
        if (!Schema.Table("automation_task_definitions").Exists())
            return;

        if (!Schema.Table("automation_task_definitions").Column("package_authorized").Exists())
        {
            Alter.Table("automation_task_definitions")
                .AddColumn("package_authorized").AsBoolean().Nullable();
        }

        if (!Schema.Table("automation_task_definitions").Column("package_warning").Exists())
        {
            Alter.Table("automation_task_definitions")
                .AddColumn("package_warning").AsString(2000).Nullable();
        }
    }

    public override void Down()
    {
        if (!Schema.Table("automation_task_definitions").Exists())
            return;

        if (Schema.Table("automation_task_definitions").Column("package_warning").Exists())
        {
            Delete.Column("package_warning").FromTable("automation_task_definitions");
        }

        if (Schema.Table("automation_task_definitions").Column("package_authorized").Exists())
        {
            Delete.Column("package_authorized").FromTable("automation_task_definitions");
        }
    }
}
