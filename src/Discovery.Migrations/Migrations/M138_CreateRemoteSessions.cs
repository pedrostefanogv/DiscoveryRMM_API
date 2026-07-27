using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260727_138)]
public class M138_CreateRemoteSessions : Migration
{
    public override void Up()
    {
        // remote_sessions
        if (!Schema.Table("remote_sessions").Exists())
        {
            Create.Table("remote_sessions")
                .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
                .WithColumn("agent_id").AsGuid().NotNullable()
                .WithColumn("user_id").AsGuid().NotNullable()
                .WithColumn("tenant_id").AsGuid().NotNullable()
                .WithColumn("site_id").AsGuid().NotNullable()
                .WithColumn("kind").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("transport").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("quality_profile").AsInt32().NotNullable().WithDefaultValue(1)
                .WithColumn("codec").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("status").AsString(32).NotNullable().WithDefaultValue("pending")
                .WithColumn("nats_subject").AsString(512).Nullable()
                .WithColumn("webrtc_session_id").AsString(256).Nullable()
                .WithColumn("recording_enabled").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("recording_id").AsGuid().Nullable()
                .WithColumn("started_at").AsDateTimeOffset().NotNullable()
                .WithColumn("expires_at").AsDateTimeOffset().NotNullable()
                .WithColumn("closed_at").AsDateTimeOffset().Nullable()
                .WithColumn("duration_seconds").AsInt32().Nullable()
                .WithColumn("frames_sent").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("bytes_sent").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("note").AsString(2000).Nullable();

            Create.Index("ix_remote_sessions_agent_id").OnTable("remote_sessions").OnColumn("agent_id");
            Create.Index("ix_remote_sessions_user_id").OnTable("remote_sessions").OnColumn("user_id");
            Create.Index("ix_remote_sessions_status").OnTable("remote_sessions").OnColumn("status");
            Create.Index("ix_remote_sessions_expires_at").OnTable("remote_sessions").OnColumn("expires_at");

            Create.ForeignKey("fk_remote_sessions_agent")
                .FromTable("remote_sessions").ForeignColumn("agent_id")
                .ToTable("agents").PrimaryColumn("id")
                .OnDelete(System.Data.Rule.None);
        }

        // remote_session_audits
        if (!Schema.Table("remote_session_audits").Exists())
        {
            Create.Table("remote_session_audits")
                .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
                .WithColumn("remote_session_id").AsGuid().NotNullable()
                .WithColumn("event_type").AsString(64).NotNullable()
                .WithColumn("actor_user_id").AsString(256).Nullable()
                .WithColumn("details").AsString(int.MaxValue).Nullable()
                .WithColumn("ip_address").AsString(64).Nullable()
                .WithColumn("occurred_at").AsDateTimeOffset().NotNullable();

            Create.Index("ix_remote_session_audits_session_id").OnTable("remote_session_audits").OnColumn("remote_session_id");
            Create.Index("ix_remote_session_audits_occurred_at").OnTable("remote_session_audits").OnColumn("occurred_at");

            Create.ForeignKey("fk_remote_session_audits_session")
                .FromTable("remote_session_audits").ForeignColumn("remote_session_id")
                .ToTable("remote_sessions").PrimaryColumn("id")
                .OnDelete(System.Data.Rule.Cascade);
        }

        // remote_session_recordings
        if (!Schema.Table("remote_session_recordings").Exists())
        {
            Create.Table("remote_session_recordings")
                .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
                .WithColumn("remote_session_id").AsGuid().NotNullable()
                .WithColumn("storage_provider").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("status").AsString(32).NotNullable().WithDefaultValue("recording")
                .WithColumn("source_codec").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("container_format").AsString(16).Nullable().WithDefaultValue("webm")
                .WithColumn("storage_url").AsString(2048).Nullable()
                .WithColumn("bytes").AsInt64().NotNullable().WithDefaultValue(0)
                .WithColumn("duration_sec").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("resolution").AsString(32).Nullable()
                .WithColumn("average_fps").AsDouble().Nullable()
                .WithColumn("started_at").AsDateTimeOffset().NotNullable()
                .WithColumn("completed_at").AsDateTimeOffset().Nullable()
                .WithColumn("started_by").AsString(256).Nullable()
                .WithColumn("retention_expires_at").AsDateTimeOffset().Nullable();

            Create.Index("ix_remote_session_recordings_session_id").OnTable("remote_session_recordings").OnColumn("remote_session_id").Unique();

            Create.ForeignKey("fk_remote_session_recordings_session")
                .FromTable("remote_session_recordings").ForeignColumn("remote_session_id")
                .ToTable("remote_sessions").PrimaryColumn("id")
                .OnDelete(System.Data.Rule.Cascade);
        }
    }

    public override void Down()
    {
        Delete.Table("remote_session_recordings");
        Delete.Table("remote_session_audits");
        Delete.Table("remote_sessions");
    }
}
