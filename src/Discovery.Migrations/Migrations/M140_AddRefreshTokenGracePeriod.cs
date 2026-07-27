using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona coluna refresh_token_grace_period_until em user_sessions.
/// Permite que múltiplas abas renovem o refresh token concorrentemente sem
/// se invalidarem mutuamente (rotação com janela de tolerância de 60s).
/// </summary>
[Migration(20260727_140)]
public class M140_AddRefreshTokenGracePeriod : Migration
{
    public override void Up()
    {
        Alter.Table("user_sessions")
            .AddColumn("refresh_token_grace_period_until").AsCustom("timestamptz").Nullable();

        // Índice para busca eficiente de tokens dentro do grace period
        Create.Index("ix_user_sessions_grace_period")
            .OnTable("user_sessions")
            .OnColumn("refresh_token_hash").Ascending()
            .OnColumn("revoked_at").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_user_sessions_grace_period").OnTable("user_sessions");
        Delete.Column("refresh_token_grace_period_until").FromTable("user_sessions");
    }
}
