using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona status tri-state (Draft/Published/Internal), departamento,
/// campos de autoria (CreatedBy/LastEditedBy) e número de versão.
/// Migra dados existentes: is_published=true → status='Published'.
/// </summary>
[Migration(20260519_060)]
public class M060_AddArticleStatusAndDepartment : Migration
{
    public override void Up()
    {
        // ── Adiciona coluna status ──────────────────────────────────
        Alter.Table("knowledge_articles")
            .AddColumn("status").AsString(20).NotNullable()
            .WithDefaultValue("Draft");

        // Migra dados existentes: is_published=true → 'Published'
        Execute.Sql(@"
            UPDATE knowledge_articles
            SET status = 'Published'
            WHERE is_published = true;
        ");

        // ── Adiciona department_id ──────────────────────────────────
        Alter.Table("knowledge_articles")
            .AddColumn("department_id").AsGuid().Nullable()
            .ForeignKey("fk_ka_department", "departments", "id");

        // ── Substitui author por created_by + last_edited_by ────────
        Alter.Table("knowledge_articles")
            .AddColumn("created_by").AsString(256).Nullable();
        Alter.Table("knowledge_articles")
            .AddColumn("last_edited_by").AsString(256).Nullable();
        Alter.Table("knowledge_articles")
            .AddColumn("last_edited_at").AsCustom("timestamptz").Nullable();

        // Migra author → created_by
        Execute.Sql(@"
            UPDATE knowledge_articles
            SET created_by = author
            WHERE author IS NOT NULL;
        ");

        // ── Adiciona current_version_number ─────────────────────────
        Alter.Table("knowledge_articles")
            .AddColumn("current_version_number").AsInt32().NotNullable()
            .WithDefaultValue(0);

        // Para artigos já publicados, versão inicial = 1
        Execute.Sql(@"
            UPDATE knowledge_articles
            SET current_version_number = 1
            WHERE status IN ('Published', 'Internal');
        ");

        // ── Novos índices ──────────────────────────────────────────
        Create.Index("ix_ka_status").OnTable("knowledge_articles").OnColumn("status");
        Create.Index("ix_ka_department_id").OnTable("knowledge_articles").OnColumn("department_id");

        // Índice composto (status, department_id) via SQL raw
        Execute.Sql(@"
            CREATE INDEX ix_ka_status_department
            ON knowledge_articles (status, department_id)
            WHERE deleted_at IS NULL;
        ");

        // Garante status válidos
        Execute.Sql(@"
            ALTER TABLE knowledge_articles
            ADD CONSTRAINT chk_ka_status
            CHECK (status IN ('Draft', 'Published', 'Internal'));
        ");
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE knowledge_articles DROP CONSTRAINT IF EXISTS chk_ka_status;");
        Delete.Index("ix_ka_status_department").OnTable("knowledge_articles");
        Delete.Index("ix_ka_department_id").OnTable("knowledge_articles");
        Delete.Index("ix_ka_status").OnTable("knowledge_articles");

        Delete.Column("current_version_number").FromTable("knowledge_articles");
        Delete.Column("last_edited_at").FromTable("knowledge_articles");
        Delete.Column("last_edited_by").FromTable("knowledge_articles");
        Delete.Column("created_by").FromTable("knowledge_articles");
        Delete.Column("department_id").FromTable("knowledge_articles");
        Delete.Column("status").FromTable("knowledge_articles");
    }
}
