using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona suporte a Object Storage em report_executions.
/// Cria nova tabela genérica 'attachments' para qualquer tipo de anexo em qualquer entidade.
/// </summary>
[Migration(20260312_060)]
public class M060_AddObjectStorageFieldsToReportExecutionAndAttachments : Migration
{
    public override void Up()
    {
        // ============ Atualizar report_executions ============
        if (!Schema.Table("report_executions").Column("storage_provider_type").Exists())
        {
            Alter.Table("report_executions")
                .AddColumn("storage_provider_type").AsInt32().NotNullable().WithDefaultValue(5); // 5 = S3Compatible
        }

        if (!Schema.Table("report_executions").Column("storage_bucket").Exists())
        {
            Alter.Table("report_executions")
                .AddColumn("storage_bucket").AsString(200).NotNullable().WithDefaultValue("");
        }

        if (!Schema.Table("report_executions").Column("storage_object_key").Exists())
        {
            Alter.Table("report_executions")
                .AddColumn("storage_object_key").AsString(1000).NotNullable().WithDefaultValue("");
        }

        if (!Schema.Table("report_executions").Column("storage_content_type").Exists())
        {
            Alter.Table("report_executions")
                .AddColumn("storage_content_type").AsString(200).NotNullable().WithDefaultValue("application/octet-stream");
        }

        if (!Schema.Table("report_executions").Column("storage_size_bytes").Exists())
        {
            Alter.Table("report_executions")
                .AddColumn("storage_size_bytes").AsInt64().NotNullable().WithDefaultValue(0);
        }

        if (!Schema.Table("report_executions").Column("storage_checksum").Exists())
        {
            Alter.Table("report_executions")
                .AddColumn("storage_checksum").AsString(200).Nullable();
        }

        if (!Schema.Table("report_executions").Column("storage_presigned_url").Exists())
        {
            Alter.Table("report_executions")
                .AddColumn("storage_presigned_url").AsString(2000).Nullable();
        }

        if (!Schema.Table("report_executions").Column("storage_presigned_url_generated_at").Exists())
        {
            Alter.Table("report_executions")
                .AddColumn("storage_presigned_url_generated_at").AsCustom("timestamptz").Nullable();
        }

        // Criar índices para queries eficientes
        if (!Schema.Table("report_executions").Index("ix_report_executions_storage_object_key").Exists())
        {
            Create.Index("ix_report_executions_storage_object_key")
                .OnTable("report_executions")
                .OnColumn("storage_object_key").Ascending();
        }

        if (!Schema.Table("report_executions").Index("ix_report_executions_created_at").Exists())
        {
            Create.Index("ix_report_executions_created_at")
                .OnTable("report_executions")
                .OnColumn("created_at").Descending();
        }

        // ============ Criar tabela genérica attachments ============
        if (!Schema.Table("attachments").Exists())
        {
            Create.Table("attachments")
                .WithColumn("id").AsGuid().PrimaryKey()
                // Referência genérica: EntityType (Ticket, Note, KnowledgeArticle, etc.) + EntityId
                .WithColumn("entity_type").AsString(100).NotNullable()
                .WithColumn("entity_id").AsGuid().NotNullable()
                // Multi-tenant: ClientId para isolamento
                .WithColumn("client_id").AsGuid().Nullable()
                // Informações do arquivo
                .WithColumn("file_name").AsString(500).NotNullable()
                .WithColumn("description").AsString(1000).Nullable()
                // Storage fields
                .WithColumn("storage_object_key").AsString(1000).NotNullable()
                .WithColumn("storage_bucket").AsString(200).NotNullable()
                .WithColumn("content_type").AsString(200).NotNullable()
                .WithColumn("size_bytes").AsInt64().NotNullable()
                .WithColumn("storage_checksum").AsString(200).Nullable()
                .WithColumn("storage_provider_type").AsInt32().NotNullable().WithDefaultValue(0)
                // Auditoria
                .WithColumn("uploaded_by").AsString(256).Nullable()
                .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
                .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
                // Soft delete
                .WithColumn("deleted_at").AsCustom("timestamptz").Nullable();
        }

        // Índices para queries eficientes
        if (!Schema.Table("attachments").Index("ix_attachments_entity_type_id").Exists())
        {
            Create.Index("ix_attachments_entity_type_id")
                .OnTable("attachments")
                .OnColumn("entity_type").Ascending()
                .OnColumn("entity_id").Ascending();
        }

        if (!Schema.Table("attachments").Index("ix_attachments_client_id").Exists())
        {
            Create.Index("ix_attachments_client_id")
                .OnTable("attachments")
                .OnColumn("client_id").Ascending();
        }

        if (!Schema.Table("attachments").Index("ix_attachments_created_at").Exists())
        {
            Create.Index("ix_attachments_created_at")
                .OnTable("attachments")
                .OnColumn("created_at").Descending();
        }

        if (!Schema.Table("attachments").Index("ix_attachments_deleted_at").Exists())
        {
            Create.Index("ix_attachments_deleted_at")
                .OnTable("attachments")
                .OnColumn("deleted_at").Ascending();
        }

        if (!Schema.Table("attachments").Index("ix_attachments_storage_object_key").Exists())
        {
            Create.Index("ix_attachments_storage_object_key")
                .OnTable("attachments")
                .OnColumn("storage_object_key").Ascending();
        }
    }

    public override void Down()
    {
        // Drop attachments table
        if (Schema.Table("attachments").Exists())
        {
            Delete.Table("attachments");
        }

        // Remove colunas de report_executions
        if (Schema.Table("report_executions").Column("storage_provider_type").Exists())
            Delete.Column("storage_provider_type").FromTable("report_executions");

        if (Schema.Table("report_executions").Column("storage_bucket").Exists())
            Delete.Column("storage_bucket").FromTable("report_executions");

        if (Schema.Table("report_executions").Column("storage_object_key").Exists())
            Delete.Column("storage_object_key").FromTable("report_executions");

        if (Schema.Table("report_executions").Column("storage_content_type").Exists())
            Delete.Column("storage_content_type").FromTable("report_executions");

        if (Schema.Table("report_executions").Column("storage_size_bytes").Exists())
            Delete.Column("storage_size_bytes").FromTable("report_executions");

        if (Schema.Table("report_executions").Column("storage_checksum").Exists())
            Delete.Column("storage_checksum").FromTable("report_executions");

        if (Schema.Table("report_executions").Column("storage_presigned_url").Exists())
            Delete.Column("storage_presigned_url").FromTable("report_executions");

        if (Schema.Table("report_executions").Column("storage_presigned_url_generated_at").Exists())
            Delete.Column("storage_presigned_url_generated_at").FromTable("report_executions");
    }
}
