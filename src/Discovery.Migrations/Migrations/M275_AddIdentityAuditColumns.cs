using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Colunas de auditoria de MUTAÇÕES DE IDENTIDADE na tabela auth_audit_logs.
///
/// A tabela nasceu (M117) só para eventos de autenticação. A auditoria de
/// 2026-11-07 mostrou que criar/editar/excluir usuário, reset de senha, revogar
/// MFA, CRUD de papel/permissão/grupo e ATRIBUIÇÃO DE PAPEL+ESCOPO não geravam
/// registro nenhum — o vetor de escalação de privilégio era invisível para
/// forense. As colunas abaixo permitem responder "quem deu este acesso, quando,
/// e o que mudou".
///
/// Tudo é aditivo e nullable: eventos de autenticação continuam válidos com esses
/// campos nulos, sem backfill e sem reescrever histórico.
/// </summary>
[Migration(20270110_275)]
public class M275_AddIdentityAuditColumns : Migration
{
    public override void Up()
    {
        if (!Schema.Table("auth_audit_logs").Column("actor_user_id").Exists())
        {
            Alter.Table("auth_audit_logs")
                .AddColumn("actor_user_id").AsGuid().Nullable()
                .AddColumn("actor_login").AsString(200).Nullable()
                .AddColumn("target_type").AsString(50).Nullable()
                .AddColumn("target_id").AsGuid().Nullable()
                .AddColumn("before_state").AsString(4000).Nullable()
                .AddColumn("after_state").AsString(4000).Nullable();
        }

        // Índices para a leitura típica: "o que aconteceu com este alvo" e
        // "o que este ator fez". Sem eles a consulta varre a tabela, que só cresce.
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_auth_audit_logs_actor
            ON auth_audit_logs (actor_user_id, occurred_at DESC);
        ");
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_auth_audit_logs_target
            ON auth_audit_logs (target_type, target_id, occurred_at DESC);
        ");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ix_auth_audit_logs_actor;");
        Execute.Sql("DROP INDEX IF EXISTS ix_auth_audit_logs_target;");

        foreach (var column in new[] { "actor_user_id", "actor_login", "target_type", "target_id", "before_state", "after_state" })
        {
            if (Schema.Table("auth_audit_logs").Column(column).Exists())
                Delete.Column(column).FromTable("auth_audit_logs");
        }
    }
}
