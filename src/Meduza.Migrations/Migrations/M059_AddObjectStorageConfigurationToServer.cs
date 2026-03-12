using FluentMigrator;

namespace Meduza.Migrations.Migrations;

/// <summary>
/// Adiciona campos de configuração de Object Storage à tabela server_configurations.
/// Permite persistir credenciais S3-compatível, bucket, endpoint, TTL de URLs, etc.
/// 
/// Campos adicionados:
/// - object_storage_bucket_name: nome do bucket global
/// - object_storage_endpoint: endpoint S3-compatível
/// - object_storage_region: região do provedor
/// - object_storage_access_key: credencial de acesso
/// - object_storage_secret_key: credencial secreta (criptografada em repouso)
/// - object_storage_url_ttl_hours: TTL de URLs pré-assinadas (default 24h)
/// - object_storage_use_path_style: flag para S3-compat path-style URLs
/// - object_storage_ssl_verify: flag para verificar SSL (false apenas em dev)
/// </summary>
[Migration(20260312_059)]
public class M059_AddObjectStorageConfigurationToServer : Migration
{
    public override void Up()
    {
        Alter.Table("server_configurations")
            // Bucket configuration
            .AddColumn("object_storage_bucket_name").AsString(200).NotNullable().WithDefaultValue("")
            .AddColumn("object_storage_endpoint").AsString(500).NotNullable().WithDefaultValue("")
            .AddColumn("object_storage_region").AsString(100).NotNullable().WithDefaultValue("")
            // Credentials (exigem encriptação em repouso no application layer)
            .AddColumn("object_storage_access_key").AsString(500).NotNullable().WithDefaultValue("")
            .AddColumn("object_storage_secret_key").AsString(1000).NotNullable().WithDefaultValue("")
            // Options
            .AddColumn("object_storage_url_ttl_hours").AsInt32().NotNullable().WithDefaultValue(24)
            .AddColumn("object_storage_use_path_style").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("object_storage_ssl_verify").AsBoolean().NotNullable().WithDefaultValue(true);
    }

    public override void Down()
    {
        Delete.Column("object_storage_bucket_name").FromTable("server_configurations");
        Delete.Column("object_storage_endpoint").FromTable("server_configurations");
        Delete.Column("object_storage_region").FromTable("server_configurations");
        Delete.Column("object_storage_access_key").FromTable("server_configurations");
        Delete.Column("object_storage_secret_key").FromTable("server_configurations");
        Delete.Column("object_storage_url_ttl_hours").FromTable("server_configurations");
        Delete.Column("object_storage_use_path_style").FromTable("server_configurations");
        Delete.Column("object_storage_ssl_verify").FromTable("server_configurations");
    }
}
