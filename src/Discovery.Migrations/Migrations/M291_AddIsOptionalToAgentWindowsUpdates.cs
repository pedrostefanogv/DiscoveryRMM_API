using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Sinaliza atualizações opcionais ou prévias (BrowseOnly / DeploymentAction=4 /
/// IsBeta / WaaS opcional) reportadas pelo agente no inventário.
/// </summary>
[Migration(20270124_291)]
public class M291_AddIsOptionalToAgentWindowsUpdates : Migration
{
    private const string Table = "agent_windows_updates";
    private const string Column = "is_optional";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;
        if (Schema.Table(Table).Column(Column).Exists())
            return;

        Execute.Sql($"ALTER TABLE {Table} ADD COLUMN {Column} boolean NOT NULL DEFAULT false");
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;
        if (Schema.Table(Table).Column(Column).Exists())
            Delete.Column(Column).FromTable(Table);
    }
}
