using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Cooldown de escalonamento por regra (minutos). O default 360 preserva o
/// comportamento anterior, que era fixo no job de monitoramento.
/// </summary>
[Migration(20260929_177)]
public class M177_AddEscalationCooldownToRules : Migration
{
    public override void Up()
    {
        if (Schema.Table("ticket_escalation_rules").Exists()
            && !Schema.Table("ticket_escalation_rules").Column("escalation_cooldown_minutes").Exists())
        {
            Alter.Table("ticket_escalation_rules")
                .AddColumn("escalation_cooldown_minutes").AsInt32().NotNullable().WithDefaultValue(360);
        }
    }

    public override void Down()
    {
        if (Schema.Table("ticket_escalation_rules").Exists()
            && Schema.Table("ticket_escalation_rules").Column("escalation_cooldown_minutes").Exists())
        {
            Delete.Column("escalation_cooldown_minutes").FromTable("ticket_escalation_rules");
        }
    }
}
