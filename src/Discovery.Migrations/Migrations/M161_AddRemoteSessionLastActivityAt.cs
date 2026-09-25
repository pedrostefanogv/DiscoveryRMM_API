using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// last_activity_at: ultimo renovamento confirmado da sessao remota (viewer
/// vivo). Alimentada pelo /renew; permite observar sessoes abandonadas sem
/// depender apenas de expires_at.
/// </summary>
[Migration(20260925_161)]
public class M161_AddRemoteSessionLastActivityAt : Migration
{
    public override void Up()
    {
        if (!Schema.Table("remote_sessions").Column("last_activity_at").Exists())
        {
            Alter.Table("remote_sessions")
                .AddColumn("last_activity_at").AsDateTimeOffset().Nullable();
        }

        Execute.Sql("UPDATE remote_sessions SET last_activity_at = started_at WHERE last_activity_at IS NULL");
    }

    public override void Down()
    {
        if (Schema.Table("remote_sessions").Column("last_activity_at").Exists())
            Delete.Column("last_activity_at").FromTable("remote_sessions");
    }
}
