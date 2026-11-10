using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Coluna <c>transports</c> em <c>user_mfa_keys</c>: guarda os transports que o autenticador
/// informou no registro (getTransports()), em CSV minúsculo ("internal,hybrid,usb").
///
/// Motivo (RELATORIO_MFA_FIDO2_ANDROID_2026-11-01.md, correção 3): allowCredentials era
/// montado só com type+id. Sem a dica de transporte, o navegador do Android não sabe que a
/// credencial é "hybrid/internal" e a descoberta no fluxo híbrido (QR + caBLE) piora —
/// exatamente o cenário da passkey do S23 que falhava. Os registros anteriores ficam com NULL
/// e o serviço simplesmente não envia transports para eles (comportamento antigo preservado).
///
/// É aditiva e nullable: nenhum backfill, nenhuma reescrita de histórico.
///
/// Numeração: 284 porque a 282 e a 283 já foram tomadas por outras tasks em andamento
/// no mesmo repositório (a 283 é o kill switch de KnowledgeEmbedding).
/// </summary>
[Migration(20270116_284)]
public class M284_AddTransportsToUserMfaKeys : Migration
{
    public override void Up()
    {
        if (!Schema.Table("user_mfa_keys").Column("transports").Exists())
        {
            Alter.Table("user_mfa_keys")
                .AddColumn("transports").AsString(256).Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("user_mfa_keys").Column("transports").Exists())
            Delete.Column("transports").FromTable("user_mfa_keys");
    }
}
