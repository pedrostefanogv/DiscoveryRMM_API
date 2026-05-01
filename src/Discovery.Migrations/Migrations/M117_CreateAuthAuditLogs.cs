using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Cria tabela de auditoria de autenticação (logins, logouts, falhas, lockouts, MFA assertions).
/// </summary>
[Migration(20260430_117, "Create AuthAuditLogs table")]
public class M117_CreateAuthAuditLogs : Migration
{
    public override void Up()
    {
        Create.Table("auth_audit_logs")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("user_id").AsGuid().Nullable().ForeignKey("users", "id")
            .WithColumn("event_type").AsString(50).NotNullable()
            .WithColumn("success").AsBoolean().NotNullable()
            .WithColumn("failure_reason").AsString(500).Nullable()
            .WithColumn("ip_address").AsString(45).Nullable()
            .WithColumn("user_agent").AsString(500).Nullable()
            .WithColumn("detail").AsString(1000).Nullable()
            .WithColumn("occurred_at").AsDateTime().NotNullable();

        Create.Index("ix_auth_audit_logs_user_id_occurred_at")
            .OnTable("auth_audit_logs")
            .OnColumn("user_id").Ascending()
            .OnColumn("occurred_at").Descending();

        Create.Index("ix_auth_audit_logs_event_type_occurred_at")
            .OnTable("auth_audit_logs")
            .OnColumn("event_type").Ascending()
            .OnColumn("occurred_at").Descending();

        Create.Index("ix_auth_audit_logs_occurred_at")
            .OnTable("auth_audit_logs")
            .OnColumn("occurred_at").Descending();
    }

    public override void Down()
    {
        Delete.Table("auth_audit_logs");
    }
}