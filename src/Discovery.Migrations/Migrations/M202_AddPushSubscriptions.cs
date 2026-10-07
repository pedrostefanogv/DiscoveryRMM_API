using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Inscricoes de Web Push (notificacoes do navegador) por usuario/dispositivo.
///
/// Uma linha por endpoint de push. O endpoint e unico globalmente: quando o
/// mesmo navegador passa para outro usuario, a inscricao e reapontada (nao
/// duplicada), garantindo que o push va para quem esta logado no navegador.
///
/// As falhas transitorias nao entram aqui: inscricoes expiradas no provedor
/// (HTTP 404/410) sao removidas pela aplicacao no momento do envio.
/// </summary>
[Migration(20261121_202)]
public class M202_AddPushSubscriptions : Migration
{
    public override void Up()
    {
        Execute.Sql(@"
            CREATE TABLE IF NOT EXISTS push_subscriptions (
                id uuid NOT NULL PRIMARY KEY,
                user_id uuid NOT NULL,
                endpoint varchar(1000) NOT NULL,
                p256dh varchar(255) NOT NULL,
                auth varchar(255) NOT NULL,
                user_agent varchar(400) NULL,
                created_at timestamptz NOT NULL,
                last_seen_at timestamptz NOT NULL
            );
        ");

        Execute.Sql(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ux_push_subscriptions_endpoint
                ON push_subscriptions (endpoint);
        ");

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_push_subscriptions_user
                ON push_subscriptions (user_id);
        ");
    }

    public override void Down()
    {
        Execute.Sql("DROP TABLE IF EXISTS push_subscriptions;");
    }
}
