using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Coluna <c>remote_sessions.rights</c>: restrições por sessão de acesso remoto
/// (ViewOnly, NoTerminal, NoFiles, LimitedInput, NoDesktop, NoEvents).
///
/// Valor é um bitmask (<c>RemoteSessionRights</c>). Default 0 = None = controle
/// total, que é exatamente o comportamento anterior — portanto sessões e bancos
/// existentes seguem funcionando sem backfill.
///
/// A imposição é feita no NATS: a credencial do viewer deixa de receber os
/// subjects das capacidades restringidas (ver RemoteSessionViewerPermissions),
/// então a restrição vale mesmo contra um cliente adulterado.
/// </summary>
[Migration(20270110_277)]
public class M277_AddRemoteSessionRights : Migration
{
    public override void Up()
    {
        if (!Schema.Table("remote_sessions").Column("rights").Exists())
        {
            Alter.Table("remote_sessions")
                .AddColumn("rights").AsInt32().NotNullable().WithDefaultValue(0);
        }
    }

    public override void Down()
    {
        if (Schema.Table("remote_sessions").Column("rights").Exists())
            Delete.Column("rights").FromTable("remote_sessions");
    }
}
