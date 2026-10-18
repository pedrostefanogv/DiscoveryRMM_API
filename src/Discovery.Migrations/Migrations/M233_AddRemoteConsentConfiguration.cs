using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Campos de autorização de acesso remoto na configuração em cascata.
///
/// Servidor tem valores base (default da coluna = Off para instalações
/// existentes: o comportamento atual é preservado até o admin ligar). Cliente e
/// site guardam override anulável (NULL = herda). Instalações novas criam a
/// linha de servidor pela entidade C#, cujo default é Prompt.
/// </summary>
[Migration(20261221_233)]
public class M233_AddRemoteConsentConfiguration : Migration
{
    private const string ServerTable = "server_configurations";
    private const string ClientTable = "client_configurations";
    private const string SiteTable = "site_configurations";

    public override void Up()
    {
        if (Schema.Table(ServerTable).Exists())
        {
            AddServerColumnIfMissing("remote_consent_mode", "int", "0");
            AddServerColumnIfMissing("remote_consent_timeout_seconds", "int", "60");
            AddServerColumnIfMissing("remote_consent_timeout_action", "int", "0");
            AddServerColumnIfMissing("remote_consent_show_banner", "boolean", "true");
            AddServerColumnIfMissing("remote_consent_message", "varchar(500)",
                "'{operator} solicita acesso remoto ao seu computador. Você autoriza?'");
            AddServerColumnIfMissing("remote_consent_banner_text", "varchar(500)",
                "'Sessão remota em andamento: {operator}'");
        }

        if (Schema.Table(ClientTable).Exists())
        {
            AddNullableColumnIfMissing(ClientTable, "remote_consent_mode", "int");
            AddNullableColumnIfMissing(ClientTable, "remote_consent_timeout_seconds", "int");
            AddNullableColumnIfMissing(ClientTable, "remote_consent_timeout_action", "int");
            AddNullableColumnIfMissing(ClientTable, "remote_consent_show_banner", "boolean");
            AddNullableColumnIfMissing(ClientTable, "remote_consent_message", "varchar(500)");
            AddNullableColumnIfMissing(ClientTable, "remote_consent_banner_text", "varchar(500)");
        }

        if (Schema.Table(SiteTable).Exists())
        {
            AddNullableColumnIfMissing(SiteTable, "remote_consent_mode", "int");
            AddNullableColumnIfMissing(SiteTable, "remote_consent_timeout_seconds", "int");
            AddNullableColumnIfMissing(SiteTable, "remote_consent_timeout_action", "int");
            AddNullableColumnIfMissing(SiteTable, "remote_consent_show_banner", "boolean");
            AddNullableColumnIfMissing(SiteTable, "remote_consent_message", "varchar(500)");
            AddNullableColumnIfMissing(SiteTable, "remote_consent_banner_text", "varchar(500)");
        }
    }

    public override void Down()
    {
        RemoveColumnIfExists(ServerTable, "remote_consent_banner_text");
        RemoveColumnIfExists(ServerTable, "remote_consent_message");
        RemoveColumnIfExists(ServerTable, "remote_consent_show_banner");
        RemoveColumnIfExists(ServerTable, "remote_consent_timeout_action");
        RemoveColumnIfExists(ServerTable, "remote_consent_timeout_seconds");
        RemoveColumnIfExists(ServerTable, "remote_consent_mode");

        foreach (var table in new[] { ClientTable, SiteTable })
        {
            RemoveColumnIfExists(table, "remote_consent_banner_text");
            RemoveColumnIfExists(table, "remote_consent_message");
            RemoveColumnIfExists(table, "remote_consent_show_banner");
            RemoveColumnIfExists(table, "remote_consent_timeout_action");
            RemoveColumnIfExists(table, "remote_consent_timeout_seconds");
            RemoveColumnIfExists(table, "remote_consent_mode");
        }
    }

    private void AddServerColumnIfMissing(string column, string sqlType, string defaultSql)
    {
        if (Schema.Table(ServerTable).Column(column).Exists())
            return;

        Execute.Sql($"ALTER TABLE {ServerTable} ADD COLUMN {column} {sqlType} NOT NULL DEFAULT {defaultSql}");
    }

    private void AddNullableColumnIfMissing(string table, string column, string sqlType)
    {
        if (Schema.Table(table).Column(column).Exists())
            return;

        Alter.Table(table).AddColumn(column).AsCustom(sqlType).Nullable();
    }

    private void RemoveColumnIfExists(string table, string column)
    {
        if (Schema.Table(table).Exists() && Schema.Table(table).Column(column).Exists())
            Delete.Column(column).FromTable(table);
    }
}
