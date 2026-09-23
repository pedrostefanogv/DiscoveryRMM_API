using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Tabela de idempotência: deduplica operações mutáveis (create/comment/close)
/// reenviadas com a mesma Idempotency-Key.
/// </summary>
[Migration(20260922_155)]
public class M155_CreateIdempotencyRecords : Migration
{
    public override void Up()
    {
        if (Schema.Table("idempotency_records").Exists())
            return;

        Create.Table("idempotency_records")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("scope").AsString(150).NotNullable()
            .WithColumn("key").AsString(200).NotNullable()
            .WithColumn("endpoint").AsString(300).NotNullable()
            .WithColumn("request_hash").AsString(64).NotNullable().WithDefaultValue("")
            .WithColumn("status_code").AsInt32().NotNullable().WithDefaultValue(-1)
            .WithColumn("response_body").AsString(int.MaxValue).Nullable()
            .WithColumn("content_type").AsString(150).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("expires_at").AsCustom("timestamptz").NotNullable();

        Create.Index("ux_idempotency_scope_key")
            .OnTable("idempotency_records")
            .OnColumn("scope").Ascending()
            .OnColumn("key").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_idempotency_expires_at")
            .OnTable("idempotency_records")
            .OnColumn("expires_at").Ascending();
    }

    public override void Down()
    {
        if (Schema.Table("idempotency_records").Exists())
            Delete.Table("idempotency_records");
    }
}
