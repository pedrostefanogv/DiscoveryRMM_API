using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Ordem da pergunta no questionário. Todas as respostas de um chamado compartilham
/// o mesmo `created_at`, então sem esta coluna a exibição caía na ordem alfabética
/// da chave em vez da ordem definida no template.
/// </summary>
[Migration(20260926_167)]
public class M167_AddSortOrderToTicketAnswers : Migration
{
    public override void Up()
    {
        if (Schema.Table("ticket_answers").Exists()
            && !Schema.Table("ticket_answers").Column("sort_order").Exists())
        {
            Alter.Table("ticket_answers")
                .AddColumn("sort_order").AsInt32().NotNullable().WithDefaultValue(0);
        }
    }

    public override void Down()
    {
        if (Schema.Table("ticket_answers").Exists()
            && Schema.Table("ticket_answers").Column("sort_order").Exists())
        {
            Delete.Column("sort_order").FromTable("ticket_answers");
        }
    }
}
