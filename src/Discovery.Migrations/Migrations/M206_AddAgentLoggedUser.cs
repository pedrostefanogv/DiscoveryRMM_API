using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Último usuário logado no Windows reportado pelo agent (sessão de console).
///
/// O valor ao vivo chega pelo heartbeat e é exibido/pesquisável em tempo real no
/// console web. Esta coluna guarda o "último conhecido" (extraído do
/// inventoryRaw.loggedInUsers reportado no inventário), para que agentes offline
/// continuem exibindo e sendo pesquisáveis pelo usuário logado.
/// </summary>
[Migration(20261125_206)]
public class M206_AddAgentLoggedUser : Migration
{
    public override void Up()
    {
        if (Schema.Table("agents").Exists()
            && !Schema.Table("agents").Column("logged_user").Exists())
        {
            Alter.Table("agents")
                .AddColumn("logged_user").AsString(256).Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("agents").Exists()
            && Schema.Table("agents").Column("logged_user").Exists())
        {
            Delete.Column("logged_user").FromTable("agents");
        }
    }
}
