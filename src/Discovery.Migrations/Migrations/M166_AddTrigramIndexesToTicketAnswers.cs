using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Acelera a busca "contém" nas respostas do questionário (pg_trgm + GIN).
/// Tolerante: se a extensão não puder ser criada (permissão do Postgres), a
/// migration não aborta — o filtro continua correto, apenas sem índice.
/// </summary>
[Migration(20260926_166)]
public class M166_AddTrigramIndexesToTicketAnswers : Migration
{
    public override void Up()
    {
        if (!Schema.Table("ticket_answers").Exists()) return;

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
                    CREATE INDEX IF NOT EXISTS ix_ticket_answers_value_trgm
                        ON ticket_answers USING gin (value_text gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS ix_ticket_answers_label_trgm
                        ON ticket_answers USING gin (question_label gin_trgm_ops);
                END IF;
            END
            $$;");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ix_ticket_answers_value_trgm;");
        Execute.Sql("DROP INDEX IF EXISTS ix_ticket_answers_label_trgm;");
    }
}
