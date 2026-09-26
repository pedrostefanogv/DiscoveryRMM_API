using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Limiar percentual de aviso de SLA por perfil de workflow. Quando nulo, o
/// monitor de SLA usa o padrão de 80%. Antes o limiar era fixo no código do job.
/// </summary>
[Migration(20260929_174)]
public class M174_AddSlaWarningPercentToWorkflowProfiles : Migration
{
    public override void Up()
    {
        if (Schema.Table("workflow_profiles").Exists()
            && !Schema.Table("workflow_profiles").Column("sla_warning_percent").Exists())
        {
            Alter.Table("workflow_profiles").AddColumn("sla_warning_percent").AsInt32().Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("workflow_profiles").Exists()
            && Schema.Table("workflow_profiles").Column("sla_warning_percent").Exists())
        {
            Delete.Column("sla_warning_percent").FromTable("workflow_profiles");
        }
    }
}
