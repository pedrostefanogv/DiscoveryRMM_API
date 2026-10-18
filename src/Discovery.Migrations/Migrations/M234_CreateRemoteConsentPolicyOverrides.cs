using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Overrides de política de consentimento de acesso remoto por usuário/grupo.
///
/// Escopo opcional por cliente/site (Guid.Empty = todos). O índice único
/// impede duas linhas para o mesmo sujeito no mesmo escopo — a resolução
/// escolhe a mais específica (site > cliente > global).
/// </summary>
[Migration(20261221_234)]
public class M234_CreateRemoteConsentPolicyOverrides : Migration
{
    private const string Table = "remote_consent_policy_overrides";
    private const string UniqueIndex = "ux_remote_consent_overrides_subject_scope";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
        {
            Create.Table(Table)
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("subject_type").AsInt32().NotNullable()
                .WithColumn("subject_id").AsGuid().NotNullable()
                .WithColumn("client_id").AsGuid().NotNullable()
                .WithColumn("site_id").AsGuid().NotNullable()
                .WithColumn("mode").AsInt32().Nullable()
                .WithColumn("timeout_seconds").AsInt32().Nullable()
                .WithColumn("timeout_action").AsInt32().Nullable()
                .WithColumn("show_banner").AsBoolean().Nullable()
                .WithColumn("message").AsString(500).Nullable()
                .WithColumn("banner_text").AsString(500).Nullable()
                .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
                .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
                .WithColumn("updated_by").AsString(256).Nullable();
        }

        if (!Schema.Table(Table).Index(UniqueIndex).Exists())
        {
            Create.Index(UniqueIndex)
                .OnTable(Table)
                .OnColumn("subject_type").Ascending()
                .OnColumn("subject_id").Ascending()
                .OnColumn("client_id").Ascending()
                .OnColumn("site_id").Ascending()
                .WithOptions().Unique();
        }

        if (!Schema.Table(Table).Index("ix_remote_consent_overrides_subject").Exists())
        {
            Create.Index("ix_remote_consent_overrides_subject")
                .OnTable(Table)
                .OnColumn("subject_type").Ascending()
                .OnColumn("subject_id").Ascending();
        }
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Index(UniqueIndex).Exists())
            Delete.Index(UniqueIndex).OnTable(Table);
        if (Schema.Table(Table).Index("ix_remote_consent_overrides_subject").Exists())
            Delete.Index("ix_remote_consent_overrides_subject").OnTable(Table);

        Delete.Table(Table);
    }
}
