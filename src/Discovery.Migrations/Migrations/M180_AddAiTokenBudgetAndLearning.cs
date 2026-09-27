using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Orçamento de tokens por departamento + aprendizado híbrido (competências e
/// recalibração de pesos). Tudo aditivo e com defaults: departamentos existentes
/// continuam no modo Sugerir e com o teto de 1200 tokens na triagem.
/// </summary>
[Migration(20261002_180)]
public class M180_AddAiTokenBudgetAndLearning : Migration
{
    public override void Up()
    {
        AddDepartmentColumns();
        AddDecisionBudgetColumns();
        CreateTechnicianSkillSuggestions();
        CreateAiWeightSuggestions();
    }

    public override void Down()
    {
        if (Schema.Table("ai_weight_suggestions").Exists()) Delete.Table("ai_weight_suggestions");
        if (Schema.Table("technician_skill_suggestions").Exists()) Delete.Table("technician_skill_suggestions");

        if (Schema.Table("ticket_assignment_decisions").Exists())
        {
            foreach (var column in new[] { "prompt_chars", "max_output_tokens" })
            {
                if (Schema.Table("ticket_assignment_decisions").Column(column).Exists())
                    Delete.Column(column).FromTable("ticket_assignment_decisions");
            }
        }

        if (Schema.Table("departments").Exists())
        {
            foreach (var column in new[]
            {
                "ai_weight_max", "ai_weight_min", "ai_weight_cycle_days",
                "ai_weight_max_delta_per_cycle", "ai_weight_learning_mode",
                "ai_skill_max_tags", "ai_skill_min_evidence", "ai_skill_learning_mode",
                "ai_assignment_max_output_tokens"
            })
            {
                if (Schema.Table("departments").Column(column).Exists())
                    Delete.Column(column).FromTable("departments");
            }
        }
    }

    private void AddDepartmentColumns()
    {
        if (!Schema.Table("departments").Exists()) return;

        if (!Schema.Table("departments").Column("ai_assignment_max_output_tokens").Exists())
            Alter.Table("departments").AddColumn("ai_assignment_max_output_tokens").AsInt32().NotNullable().WithDefaultValue(1200);

        if (!Schema.Table("departments").Column("ai_skill_learning_mode").Exists())
            Alter.Table("departments").AddColumn("ai_skill_learning_mode").AsInt32().NotNullable().WithDefaultValue(1);

        if (!Schema.Table("departments").Column("ai_skill_min_evidence").Exists())
            Alter.Table("departments").AddColumn("ai_skill_min_evidence").AsInt32().NotNullable().WithDefaultValue(3);

        if (!Schema.Table("departments").Column("ai_skill_max_tags").Exists())
            Alter.Table("departments").AddColumn("ai_skill_max_tags").AsInt32().NotNullable().WithDefaultValue(12);

        if (!Schema.Table("departments").Column("ai_weight_learning_mode").Exists())
            Alter.Table("departments").AddColumn("ai_weight_learning_mode").AsInt32().NotNullable().WithDefaultValue(1);

        if (!Schema.Table("departments").Column("ai_weight_max_delta_per_cycle").Exists())
            Alter.Table("departments").AddColumn("ai_weight_max_delta_per_cycle").AsDecimal(4, 3).NotNullable().WithDefaultValue(0.100);

        if (!Schema.Table("departments").Column("ai_weight_cycle_days").Exists())
            Alter.Table("departments").AddColumn("ai_weight_cycle_days").AsInt32().NotNullable().WithDefaultValue(7);

        if (!Schema.Table("departments").Column("ai_weight_min").Exists())
            Alter.Table("departments").AddColumn("ai_weight_min").AsDecimal(4, 3).NotNullable().WithDefaultValue(0.050);

        if (!Schema.Table("departments").Column("ai_weight_max").Exists())
            Alter.Table("departments").AddColumn("ai_weight_max").AsDecimal(4, 3).NotNullable().WithDefaultValue(0.500);
    }

    private void AddDecisionBudgetColumns()
    {
        if (!Schema.Table("ticket_assignment_decisions").Exists()) return;

        if (!Schema.Table("ticket_assignment_decisions").Column("max_output_tokens").Exists())
            Alter.Table("ticket_assignment_decisions").AddColumn("max_output_tokens").AsInt32().NotNullable().WithDefaultValue(0);

        if (!Schema.Table("ticket_assignment_decisions").Column("prompt_chars").Exists())
            Alter.Table("ticket_assignment_decisions").AddColumn("prompt_chars").AsInt32().NotNullable().WithDefaultValue(0);
    }

    private void CreateTechnicianSkillSuggestions()
    {
        if (Schema.Table("technician_skill_suggestions").Exists()) return;

        Create.Table("technician_skill_suggestions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("department_id").AsGuid().NotNullable()
            .WithColumn("user_id").AsGuid().NotNullable()
            .WithColumn("window_days").AsInt32().NotNullable().WithDefaultValue(90)
            .WithColumn("suggested_tags_json").AsCustom("jsonb").NotNullable().WithDefaultValue("[]")
            .WithColumn("applied_tags_json").AsCustom("jsonb").Nullable()
            .WithColumn("evidence_json").AsCustom("jsonb").Nullable()
            .WithColumn("status").AsString(20).NotNullable()
            .WithColumn("auto_applied").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("decided_at").AsCustom("timestamptz").Nullable()
            .WithColumn("decided_by_user_id").AsGuid().Nullable();

        Create.ForeignKey("fk_technician_skill_suggestions_department")
            .FromTable("technician_skill_suggestions").ForeignColumn("department_id")
            .ToTable("departments").PrimaryColumn("id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.ForeignKey("fk_technician_skill_suggestions_user")
            .FromTable("technician_skill_suggestions").ForeignColumn("user_id")
            .ToTable("users").PrimaryColumn("id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("ix_technician_skill_suggestions_department_created")
            .OnTable("technician_skill_suggestions")
            .OnColumn("department_id").Ascending()
            .OnColumn("created_at").Descending();

        Create.Index("ix_technician_skill_suggestions_department_user")
            .OnTable("technician_skill_suggestions")
            .OnColumn("department_id").Ascending()
            .OnColumn("user_id").Ascending();
    }

    private void CreateAiWeightSuggestions()
    {
        if (Schema.Table("ai_weight_suggestions").Exists()) return;

        Create.Table("ai_weight_suggestions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("department_id").AsGuid().NotNullable()
            .WithColumn("cycle_days").AsInt32().NotNullable().WithDefaultValue(7)
            .WithColumn("window_start").AsCustom("timestamptz").NotNullable()
            .WithColumn("window_end").AsCustom("timestamptz").NotNullable()
            .WithColumn("current_weights_json").AsCustom("jsonb").NotNullable().WithDefaultValue("{}")
            .WithColumn("suggested_weights_json").AsCustom("jsonb").NotNullable().WithDefaultValue("{}")
            .WithColumn("evidence_json").AsCustom("jsonb").Nullable()
            .WithColumn("status").AsString(20).NotNullable()
            .WithColumn("auto_applied").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("decided_at").AsCustom("timestamptz").Nullable()
            .WithColumn("decided_by_user_id").AsGuid().Nullable();

        Create.ForeignKey("fk_ai_weight_suggestions_department")
            .FromTable("ai_weight_suggestions").ForeignColumn("department_id")
            .ToTable("departments").PrimaryColumn("id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("ix_ai_weight_suggestions_department_created")
            .OnTable("ai_weight_suggestions")
            .OnColumn("department_id").Ascending()
            .OnColumn("created_at").Descending();
    }
}
