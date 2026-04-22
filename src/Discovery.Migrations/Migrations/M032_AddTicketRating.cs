using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260306_032)]
public class M032_AddTicketRating : Migration
{
    public override void Up()
    {
        // Adiciona colunas para avaliação/rating de tickets (0-5 estrelas)
        Alter.Table("tickets")
            .AddColumn("rating").AsInt32().Nullable()
            .AddColumn("rated_at").AsCustom("timestamptz").Nullable()
            .AddColumn("rated_by").AsString(255).Nullable();

        // CHECK constraint permanece em SQL porque no FluentMigrator 8.0.1
        // não há API fluente equivalente para criação/remoção deste tipo de constraint.
        Execute.Sql(@"
            ALTER TABLE tickets 
            ADD CONSTRAINT chk_tickets_rating 
            CHECK (rating IS NULL OR (rating >= 0 AND rating <= 5))
        ");

        // Cria índice para otimizar queries que filtram por rating
        Create.Index("ix_tickets_rating")
            .OnTable("tickets")
            .OnColumn("rating");
    }

    public override void Down()
    {
        // Mantém SQL direto para remover o CHECK, pelo mesmo motivo da criação no Up.
        Execute.Sql("ALTER TABLE tickets DROP CONSTRAINT IF EXISTS chk_tickets_rating");
        Delete.Index("ix_tickets_rating").OnTable("tickets");
        Delete.Column("rating").FromTable("tickets");
        Delete.Column("rated_at").FromTable("tickets");
        Delete.Column("rated_by").FromTable("tickets");
    }
}
