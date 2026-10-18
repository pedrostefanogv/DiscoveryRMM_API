using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Colunas de consentimento na própria sessão remota.
///
/// A M233 só cobriu a configuração em cascata; a entidade RemoteSession ganhou
/// os campos de decisão (deadline/outcome/origem) e esta migration fecha a
/// lacuna do schema. Sem ela as consultas do EF quebram com 42703
/// ("column r.consent_deadline_at does not exist") no start e no sweeper.
/// </summary>
[Migration(20261221_235)]
public class M235_AddRemoteSessionConsentColumns : Migration
{
    private const string Table = "remote_sessions";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        AddColumnIfMissing("consent_required", "boolean", "false");
        AddColumnIfMissing("consent_deadline_at", "timestamptz", null);
        AddColumnIfMissing("consent_decided_at", "timestamptz", null);
        AddColumnIfMissing("consent_outcome", "varchar(64)", null);
        AddColumnIfMissing("consent_policy_source", "varchar(64)", null);
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        foreach (var column in new[]
                 {
                     "consent_policy_source",
                     "consent_outcome",
                     "consent_decided_at",
                     "consent_deadline_at",
                     "consent_required"
                 })
        {
            if (Schema.Table(Table).Column(column).Exists())
                Delete.Column(column).FromTable(Table);
        }
    }

    private void AddColumnIfMissing(string column, string sqlType, string? defaultSql)
    {
        if (Schema.Table(Table).Column(column).Exists())
            return;

        var sql = $"ALTER TABLE {Table} ADD COLUMN {column} {sqlType}";
        if (defaultSql is not null)
            sql += $" NOT NULL DEFAULT {defaultSql}";
        else
            sql += " NULL";
        Execute.Sql(sql);
    }
}
