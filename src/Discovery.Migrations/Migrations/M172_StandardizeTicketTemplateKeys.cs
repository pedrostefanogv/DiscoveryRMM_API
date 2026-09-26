using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Padroniza a chave dos templates de chamado: `name` passa a ser um
/// identificador ([a-z0-9_]) e ganha unicidade dentro do escopo
/// (cliente + departamento), com NULLs contando como iguais — ou seja,
/// templates globais também são únicos entre si.
///
/// 1) backfill: nomes atuais viram slug (sem acento, minúsculo, _ no lugar de
///    espaços/símbolos); duplicatas do mesmo escopo recebem sufixo _2, _3…;
/// 2) índice único NULLS NOT DISTINCT (PostgreSQL 15+), criado só se não
///    restar duplicata (senão a migração só avisa e a API valida).
/// </summary>
[Migration(20260928_172)]
public class M172_StandardizeTicketTemplateKeys : Migration
{
    private const string UniqueIndexName = "ux_ticket_templates_scope_name";

    public override void Up()
    {
        if (!Schema.Table("ticket_templates").Exists()) return;

        // 1) slug dos nomes existentes
        Execute.Sql(@"
            UPDATE ticket_templates
               SET name = COALESCE(NULLIF(
                     trim(both '_' from regexp_replace(
                       translate(lower(name),
                         'áàâãäéèêëíìîïóòôõöúùûüçñýÿ',
                         'aaaaaeeeeiiiiooooouuuucnyy'),
                       '[^a-z0-9]+', '_', 'g')),
                     ''), 'template')");

        // 2) desempata duplicatas dentro do mesmo escopo (globais inclusive).
        // Repete até estabilizar: um sufixo pode colidir com um slug que já
        // existia (ex.: "x_2"), e sem isso o índice único seria pulado.
        Execute.Sql(@"
            DO $$
            DECLARE
                pendentes integer;
                rodada integer := 0;
            BEGIN
                LOOP
                    WITH ranked AS (
                        SELECT id,
                               name AS base,
                               row_number() OVER (
                                   PARTITION BY client_id, department_id, name
                                   ORDER BY created_at, id
                               ) AS rn
                          FROM ticket_templates
                    )
                    UPDATE ticket_templates t
                       SET name = CASE
                             WHEN rodada = 0 THEN left(r.base, 74) || '_' || r.rn
                             ELSE left(r.base, 68) || '_' || r.rn || '_' || rodada
                           END
                      FROM ranked r
                     WHERE t.id = r.id AND r.rn > 1;

                    SELECT count(*) INTO pendentes FROM (
                        SELECT 1 FROM ticket_templates
                         GROUP BY client_id, department_id, name
                        HAVING count(*) > 1
                    ) d;

                    EXIT WHEN pendentes = 0 OR rodada >= 5;
                    rodada := rodada + 1;
                END LOOP;

                IF pendentes > 0 THEN
                    RAISE NOTICE 'M172: % chave(s) ainda duplicada(s) após o desempate.', pendentes;
                END IF;
            END $$;");

        // 3) índice único (guardado: nunca falha a migração por dado legado)
        Execute.Sql($@"
            DO $$
            DECLARE
                duplicados integer;
            BEGIN
                SELECT count(*) INTO duplicados FROM (
                    SELECT 1
                      FROM ticket_templates
                     GROUP BY client_id, department_id, name
                    HAVING count(*) > 1
                ) d;

                IF duplicados > 0 THEN
                    RAISE NOTICE 'M172: {UniqueIndexName} não criado — % grupo(s) de chave duplicada (a API valida na gravação).', duplicados;
                    RETURN;
                END IF;

                BEGIN
                    CREATE UNIQUE INDEX IF NOT EXISTS {UniqueIndexName}
                        ON ticket_templates (client_id, department_id, name) NULLS NOT DISTINCT;
                EXCEPTION
                    -- PostgreSQL < 15 não conhece NULLS NOT DISTINCT: não pode
                    -- derrubar a migração (a API valida a unicidade).
                    WHEN syntax_error OR feature_not_supported THEN
                        RAISE NOTICE 'M172: servidor sem suporte a NULLS NOT DISTINCT — {UniqueIndexName} não criado (a API valida a unicidade).';
                END;
            END $$;");
    }

    public override void Down()
    {
        if (!Schema.Table("ticket_templates").Exists()) return;

        Execute.Sql($"DROP INDEX IF EXISTS {UniqueIndexName};");
    }
}
