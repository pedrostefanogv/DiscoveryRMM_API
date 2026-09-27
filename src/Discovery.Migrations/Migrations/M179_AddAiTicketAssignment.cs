using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Triagem por IA na auto-atribuição de chamados: configuração no departamento,
/// perfil de competências do membro, métricas materializadas por atendente,
/// decisões auditáveis e fila assíncrona de triagem.
///
/// Tudo aditivo e com defaults: departamentos existentes mantêm as estratégias
/// 0/1/2 e nenhum comportamento muda até a estratégia AiTriage ser escolhida.
/// </summary>
[Migration(20261001_179)]
public class M179_AddAiTicketAssignment : Migration
{
    public override void Up()
    {
        AddDepartmentAiColumns();
        AddDepartmentMemberProfileColumns();
        CreateTechnicianMetricsSnapshots();
        CreateTicketAssignmentDecisions();
        CreateAiAssignmentQueue();
        AddTicketTrigramIndexes();
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ix_tickets_search_text_trgm;");
        Execute.Sql("DROP INDEX IF EXISTS ix_tickets_title_trgm;");
        Execute.Sql("DROP INDEX IF EXISTS ix_tickets_description_trgm;");

        if (Schema.Table("ai_assignment_queue").Exists()) Delete.Table("ai_assignment_queue");
        if (Schema.Table("ticket_assignment_decisions").Exists()) Delete.Table("ticket_assignment_decisions");
        if (Schema.Table("technician_metrics_snapshots").Exists()) Delete.Table("technician_metrics_snapshots");

        if (Schema.Table("department_members").Exists())
        {
            foreach (var column in new[]
            {
                "accepts_ai_assignment", "weight", "max_open_tickets", "skill_level", "skill_tags_json"
            })
            {
                if (Schema.Table("department_members").Column(column).Exists())
                    Delete.Column(column).FromTable("department_members");
            }
        }

        if (Schema.Table("departments").Exists())
        {
            foreach (var column in new[]
            {
                "ai_assignment_use_affinity", "ai_assignment_instructions", "ai_assignment_weights_json",
                "ai_assignment_max_candidates", "ai_assignment_fallback_strategy",
                "ai_assignment_min_confidence", "ai_assignment_mode"
            })
            {
                if (Schema.Table("departments").Column(column).Exists())
                    Delete.Column(column).FromTable("departments");
            }
        }
    }

    private void AddDepartmentAiColumns()
    {
        if (!Schema.Table("departments").Exists()) return;

        if (!Schema.Table("departments").Column("ai_assignment_mode").Exists())
            Alter.Table("departments").AddColumn("ai_assignment_mode").AsInt32().NotNullable().WithDefaultValue(0);

        if (!Schema.Table("departments").Column("ai_assignment_min_confidence").Exists())
            Alter.Table("departments").AddColumn("ai_assignment_min_confidence").AsDecimal(4, 3).NotNullable().WithDefaultValue(0.600);

        if (!Schema.Table("departments").Column("ai_assignment_fallback_strategy").Exists())
            Alter.Table("departments").AddColumn("ai_assignment_fallback_strategy").AsInt32().NotNullable().WithDefaultValue(1);

        if (!Schema.Table("departments").Column("ai_assignment_max_candidates").Exists())
            Alter.Table("departments").AddColumn("ai_assignment_max_candidates").AsInt32().NotNullable().WithDefaultValue(8);

        if (!Schema.Table("departments").Column("ai_assignment_weights_json").Exists())
            Alter.Table("departments").AddColumn("ai_assignment_weights_json").AsCustom("jsonb").Nullable();

        if (!Schema.Table("departments").Column("ai_assignment_instructions").Exists())
            Alter.Table("departments").AddColumn("ai_assignment_instructions").AsCustom("text").Nullable();

        if (!Schema.Table("departments").Column("ai_assignment_use_affinity").Exists())
            Alter.Table("departments").AddColumn("ai_assignment_use_affinity").AsBoolean().NotNullable().WithDefaultValue(true);
    }

    private void AddDepartmentMemberProfileColumns()
    {
        if (!Schema.Table("department_members").Exists()) return;

        if (!Schema.Table("department_members").Column("skill_tags_json").Exists())
            Alter.Table("department_members").AddColumn("skill_tags_json").AsCustom("jsonb").Nullable();

        if (!Schema.Table("department_members").Column("skill_level").Exists())
            Alter.Table("department_members").AddColumn("skill_level").AsInt32().NotNullable().WithDefaultValue(3);

        if (!Schema.Table("department_members").Column("max_open_tickets").Exists())
            Alter.Table("department_members").AddColumn("max_open_tickets").AsInt32().Nullable();

        if (!Schema.Table("department_members").Column("weight").Exists())
            Alter.Table("department_members").AddColumn("weight").AsDecimal(4, 2).NotNullable().WithDefaultValue(1.0);

        if (!Schema.Table("department_members").Column("accepts_ai_assignment").Exists())
            Alter.Table("department_members").AddColumn("accepts_ai_assignment").AsBoolean().NotNullable().WithDefaultValue(true);
    }

    private void CreateTechnicianMetricsSnapshots()
    {
        if (Schema.Table("technician_metrics_snapshots").Exists()) return;

        Create.Table("technician_metrics_snapshots")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
            .WithColumn("window_days").AsInt32().NotNullable().WithDefaultValue(90)
            .WithColumn("computed_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("assigned_total").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("resolved_total").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("open_now").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("avg_first_response_minutes").AsCustom("double precision").Nullable()
            .WithColumn("avg_resolution_minutes").AsCustom("double precision").Nullable()
            .WithColumn("p90_resolution_minutes").AsCustom("double precision").Nullable()
            .WithColumn("sla_breach_rate").AsCustom("double precision").NotNullable().WithDefaultValue(0)
            .WithColumn("reopen_rate").AsCustom("double precision").NotNullable().WithDefaultValue(0)
            .WithColumn("csat_average").AsCustom("double precision").Nullable()
            .WithColumn("csat_rated_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("difficulty_average").AsCustom("double precision").Nullable()
            .WithColumn("top_categories_json").AsCustom("jsonb").Nullable()
            .WithColumn("top_tags_json").AsCustom("jsonb").Nullable();

        Create.ForeignKey("fk_technician_metrics_snapshots_user")
            .FromTable("technician_metrics_snapshots").ForeignColumn("user_id")
            .ToTable("users").PrimaryColumn("id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("ux_technician_metrics_snapshots_user")
            .OnTable("technician_metrics_snapshots")
            .OnColumn("user_id").Ascending()
            .WithOptions().Unique();
    }

    private void CreateTicketAssignmentDecisions()
    {
        if (Schema.Table("ticket_assignment_decisions").Exists()) return;

        Create.Table("ticket_assignment_decisions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("ticket_id").AsGuid().NotNullable()
            .WithColumn("department_id").AsGuid().NotNullable()
            .WithColumn("mode").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("strategy_source").AsString(40).NotNullable()
            .WithColumn("difficulty").AsInt32().NotNullable().WithDefaultValue(3)
            .WithColumn("chosen_user_id").AsGuid().Nullable()
            .WithColumn("confidence").AsCustom("double precision").NotNullable().WithDefaultValue(0)
            .WithColumn("score").AsCustom("double precision").NotNullable().WithDefaultValue(0)
            .WithColumn("candidates_json").AsCustom("jsonb").Nullable()
            .WithColumn("rationale").AsCustom("text").Nullable()
            .WithColumn("model").AsString(200).Nullable()
            .WithColumn("tokens_used").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("applied").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("not_applied_reason").AsCustom("text").Nullable()
            .WithColumn("overridden_at").AsCustom("timestamptz").Nullable()
            .WithColumn("overridden_by_user_id").AsGuid().Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable();

        Create.ForeignKey("fk_ticket_assignment_decisions_ticket")
            .FromTable("ticket_assignment_decisions").ForeignColumn("ticket_id")
            .ToTable("tickets").PrimaryColumn("id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("ix_ticket_assignment_decisions_ticket")
            .OnTable("ticket_assignment_decisions")
            .OnColumn("ticket_id").Ascending();

        Create.Index("ix_ticket_assignment_decisions_department_created")
            .OnTable("ticket_assignment_decisions")
            .OnColumn("department_id").Ascending()
            .OnColumn("created_at").Descending();
    }

    private void CreateAiAssignmentQueue()
    {
        if (Schema.Table("ai_assignment_queue").Exists()) return;

        Create.Table("ai_assignment_queue")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("ticket_id").AsGuid().NotNullable()
            .WithColumn("department_id").AsGuid().NotNullable()
            .WithColumn("status").AsString(20).NotNullable()
            .WithColumn("attempts").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("available_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("last_error").AsCustom("text").Nullable()
            .WithColumn("reason").AsString(80).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable();

        Create.ForeignKey("fk_ai_assignment_queue_ticket")
            .FromTable("ai_assignment_queue").ForeignColumn("ticket_id")
            .ToTable("tickets").PrimaryColumn("id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("ux_ai_assignment_queue_ticket")
            .OnTable("ai_assignment_queue")
            .OnColumn("ticket_id").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_ai_assignment_queue_status_available")
            .OnTable("ai_assignment_queue")
            .OnColumn("status").Ascending()
            .OnColumn("available_at").Ascending();

        Create.Index("ix_ai_assignment_queue_updated_at")
            .OnTable("ai_assignment_queue")
            .OnColumn("updated_at").Ascending();
    }

    /// <summary>
    /// Afinidade textual (similaridade entre chamados) via pg_trgm. Tolerante: se
    /// a extensão não puder ser criada, a migration não aborta — a triagem
    /// apenas deixa de usar o sinal de afinidade.
    ///
    /// O índice é criado sobre a EXPRESSÃO usada na consulta de afinidade
    /// (título + descrição), não sobre as colunas isoladas: um índice apenas nas
    /// colunas não é utilizado pelo operador de similaridade sobre a concatenação
    /// e a busca viraria varredura completa em tickets.
    /// </summary>
    private void AddTicketTrigramIndexes()
    {
        if (!Schema.Table("tickets").Exists()) return;

        Execute.Sql(@"
            DO $$
            BEGIN
                BEGIN
                    CREATE EXTENSION IF NOT EXISTS pg_trgm;
                EXCEPTION WHEN OTHERS THEN
                    RAISE NOTICE 'pg_trgm indisponivel: %', SQLERRM;
                END;
            END
            $$;");

        Execute.Sql(@"
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm') THEN
                    CREATE INDEX IF NOT EXISTS ix_tickets_search_text_trgm
                        ON tickets USING gin ((title || ' ' || coalesce(description, '')) gin_trgm_ops);
                END IF;
            END
            $$;");
    }
}
