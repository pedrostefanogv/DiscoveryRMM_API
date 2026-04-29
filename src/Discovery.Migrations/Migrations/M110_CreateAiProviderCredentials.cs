using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Cria a tabela ai_provider_credentials para credenciais AI por escopo (Global/Client/Site)
/// com herança hierárquica: Site → Client → Global.
/// </summary>
[Migration(20260429_110)]
public class M110_CreateAiProviderCredentials : Migration
{
    public override void Up()
    {
        Create.Table("ai_provider_credentials")
            .WithColumn("id").AsGuid().PrimaryKey().NotNullable()
            .WithColumn("scope_type").AsInt32().NotNullable()  // AppApprovalScopeType: 0=Global, 1=Client, 2=Site
            .WithColumn("client_id").AsGuid().Nullable()
            .WithColumn("site_id").AsGuid().Nullable()
            .WithColumn("provider").AsString(50).NotNullable().WithDefaultValue("openai")
            .WithColumn("base_url").AsString(512).Nullable()
            .WithColumn("embedding_base_url").AsString(512).Nullable()
            .WithColumn("api_key_encrypted").AsString(int.MaxValue).Nullable()
            .WithColumn("embedding_api_key_encrypted").AsString(int.MaxValue).Nullable()
            .WithColumn("key_fingerprint_hash").AsString(128).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by").AsString(256).Nullable()
            .WithColumn("updated_by").AsString(256).Nullable();

        // Índice para busca hierárquica (Global + Client + Site)
        Create.Index("ix_ai_provider_credentials_hierarchy")
            .OnTable("ai_provider_credentials")
            .OnColumn("client_id").Ascending()
            .OnColumn("site_id").Ascending()
            .OnColumn("scope_type").Ascending();

        // Índice único: apenas uma credencial por escopo+provider (evita duplicata)
        Create.Index("ix_ai_provider_credentials_scope_provider_unique")
            .OnTable("ai_provider_credentials")
            .OnColumn("provider").Ascending()
            .OnColumn("client_id").Ascending()
            .OnColumn("site_id").Ascending()
            .WithOptions().Unique();
    }

    public override void Down()
    {
        Delete.Index("ix_ai_provider_credentials_scope_provider_unique").OnTable("ai_provider_credentials");
        Delete.Index("ix_ai_provider_credentials_hierarchy").OnTable("ai_provider_credentials");
        Delete.Table("ai_provider_credentials");
    }
}
