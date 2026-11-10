using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Kill switch do job de embeddings da base de conhecimento (KnowledgeEmbeddingJob).
///
/// O job (re-chunking de artigos, geracao de embeddings em lote e consumo da fila
/// LISTEN/NOTIFY) era agendado INCONDICIONALMENTE a cada 30s em
/// QuartzServiceCollectionExtensions, e a chave
/// BackgroundJobs:KnowledgeEmbeddingEnabled do appsettings.json nao era lida por
/// nenhum codigo — o flag dizia "false" enquanto o job rodava.
///
/// Esta coluna torna o kill switch real e controlavel pela UI, com a mesma
/// semantica de heranca das demais features (ZeroTouchEnabled / UpdatesTabEnabled):
///   server_configurations.knowledge_embedding_enabled -> NOT NULL DEFAULT true
///     (default global HABILITADO: zero mudanca de comportamento no rollout);
///   client_configurations.knowledge_embedding_enabled -> NULLABLE (null = herda servidor);
///   site_configurations.knowledge_embedding_enabled   -> NULLABLE (null = herda cliente/servidor).
///
/// Precedencia efetiva: site ?? client ?? server.
///
/// Nao ha UPDATE/backfill retroativo de proposito: o DEFAULT true do servidor ja
/// preserva o comportamento atual para toda a frota existente.
/// </summary>
[Migration(20270114_283)]
public class M283_AddKnowledgeEmbeddingEnabled : Migration
{
    public override void Up()
    {
        if (Schema.Table("server_configurations").Exists()
            && !Schema.Table("server_configurations").Column("knowledge_embedding_enabled").Exists())
        {
            Alter.Table("server_configurations")
                .AddColumn("knowledge_embedding_enabled").AsBoolean().NotNullable().WithDefaultValue(true);
        }

        if (Schema.Table("client_configurations").Exists()
            && !Schema.Table("client_configurations").Column("knowledge_embedding_enabled").Exists())
        {
            Alter.Table("client_configurations")
                .AddColumn("knowledge_embedding_enabled").AsBoolean().Nullable();
        }

        if (Schema.Table("site_configurations").Exists()
            && !Schema.Table("site_configurations").Column("knowledge_embedding_enabled").Exists())
        {
            Alter.Table("site_configurations")
                .AddColumn("knowledge_embedding_enabled").AsBoolean().Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("site_configurations").Exists()
            && Schema.Table("site_configurations").Column("knowledge_embedding_enabled").Exists())
        {
            Delete.Column("knowledge_embedding_enabled").FromTable("site_configurations");
        }

        if (Schema.Table("client_configurations").Exists()
            && Schema.Table("client_configurations").Column("knowledge_embedding_enabled").Exists())
        {
            Delete.Column("knowledge_embedding_enabled").FromTable("client_configurations");
        }

        if (Schema.Table("server_configurations").Exists()
            && Schema.Table("server_configurations").Column("knowledge_embedding_enabled").Exists())
        {
            Delete.Column("knowledge_embedding_enabled").FromTable("server_configurations");
        }
    }
}