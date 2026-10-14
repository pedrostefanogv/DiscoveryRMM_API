using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona server_configurations.nats_web_socket_external_url.
///
/// A propriedade sempre existiu no EF Core
/// (ServerConfiguration.NatsWebSocketExternalUrl mapeado em
/// DiscoveryDbContext.Configurations.cs), mas NENHUMA migration criava a coluna.
/// Em banco novo a API subia e falhava em runtime ao ler server_configurations:
/// "42703: column s.nats_web_socket_external_url does not exist"
/// (ServerConfigurationRepository -> ReportService -> ReportGenerationJob),
/// derrubando o host em loop de restart.
///
/// Mesmo padrao da coluna vizinha nats_server_host_external (M081):
/// text NOT NULL DEFAULT ''.
/// </summary>
[Migration(20261215_225)]
public class M225_AddNatsWebSocketExternalUrl : Migration
{
    private const string Table = "server_configurations";
    private const string Column = "nats_web_socket_external_url";

    public override void Up()
    {
        if (!Schema.Table(Table).Column(Column).Exists())
        {
            Alter.Table(Table)
                .AddColumn(Column).AsCustom("text").NotNullable().WithDefaultValue("");
        }
    }

    public override void Down()
    {
        if (Schema.Table(Table).Column(Column).Exists())
        {
            Delete.Column(Column).FromTable(Table);
        }
    }
}
