using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Gravação incremental do terminal remoto (WP3). O agente publica frames em
/// <c>tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.recording.term</c>;
/// o servidor persiste o stream em NDJSON e mantém esta linha com os contadores
/// agregados, status e retenção.
///
/// Uma gravação por sessão (índice único em remote_session_id). O arquivo em si
/// não fica no banco: storage_url aponta para o caminho relativo no storage local.
///
/// ATENÇÃO: esta migração NÃO foi aplicada em nenhum banco.
/// </summary>
[Migration(20261218_228)]
public class M228_CreateRemoteTerminalRecordings : Migration
{
    private const string Table = "remote_terminal_recordings";

    public override void Up()
    {
        if (Schema.Table(Table).Exists())
            return;

        Create.Table(Table)
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("remote_session_id").AsGuid().NotNullable()
                // Cascade igual ao EF (DeleteBehavior.Cascade) e à M138: sem isso
                // excluir uma sessão remota falha por FK se a linha do terminal
                // não tiver sido apagada antes.
                .ForeignKey("fk_remote_terminal_recordings_session", "remote_sessions", "id")
                .OnDelete(System.Data.Rule.Cascade)
            .WithColumn("agent_id").AsGuid().NotNullable()
            .WithColumn("client_id").AsGuid().NotNullable()
            .WithColumn("status").AsString(32).NotNullable().WithDefaultValue("recording")
            .WithColumn("started_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("last_frame_at").AsCustom("timestamptz").Nullable()
            .WithColumn("completed_at").AsCustom("timestamptz").Nullable()
            .WithColumn("frames").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("bytes").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("dropped_frames").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("max_fps").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("storage_provider").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("storage_url").AsString(2048).Nullable()
            .WithColumn("retention_expires_at").AsCustom("timestamptz").Nullable()
            .WithColumn("last_error").AsString(2000).Nullable();

        Create.Index("ix_remote_terminal_recordings_session_id")
            .OnTable(Table)
            .OnColumn("remote_session_id").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_remote_terminal_recordings_retention_expires_at")
            .OnTable(Table)
            .OnColumn("retention_expires_at").Ascending();

        Create.Index("ix_remote_terminal_recordings_agent_id")
            .OnTable(Table)
            .OnColumn("agent_id").Ascending();
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Index("ix_remote_terminal_recordings_agent_id").Exists())
            Delete.Index("ix_remote_terminal_recordings_agent_id").OnTable(Table);

        if (Schema.Table(Table).Index("ix_remote_terminal_recordings_retention_expires_at").Exists())
            Delete.Index("ix_remote_terminal_recordings_retention_expires_at").OnTable(Table);

        if (Schema.Table(Table).Index("ix_remote_terminal_recordings_session_id").Exists())
            Delete.Index("ix_remote_terminal_recordings_session_id").OnTable(Table);

        Delete.Table(Table);
    }
}
