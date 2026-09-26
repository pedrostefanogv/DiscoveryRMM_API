using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Modelos de campos personalizados pré-configurados + máscara de entrada nos
/// campos e snapshot markdown (somente leitura) do formulário enviado na abertura
/// do chamado.
/// </summary>
[Migration(20260926_162)]
public class M162_CreateCustomFieldTemplatesAndSubmissionSnapshot : Migration
{
    public override void Up()
    {
        if (!Schema.Table("custom_field_templates").Exists())
        {
            Create.Table("custom_field_templates")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("client_id").AsGuid().Nullable()
                .WithColumn("department_id").AsGuid().Nullable()
                .WithColumn("name").AsString(200).NotNullable()
                .WithColumn("label").AsString(200).NotNullable()
                .WithColumn("description").AsString(1000).Nullable()
                .WithColumn("data_type").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("options_json").AsCustom("jsonb").Nullable()
                .WithColumn("validation_regex").AsString(500).Nullable()
                .WithColumn("input_mask").AsString(100).Nullable()
                .WithColumn("min_length").AsInt32().Nullable()
                .WithColumn("max_length").AsInt32().Nullable()
                .WithColumn("min_value").AsCustom("numeric(18,6)").Nullable()
                .WithColumn("max_value").AsCustom("numeric(18,6)").Nullable()
                .WithColumn("default_is_required").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("is_built_in").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("sort_order").AsInt32().NotNullable().WithDefaultValue(0)
                .WithColumn("created_by").AsString(255).Nullable()
                .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
                .WithColumn("updated_at").AsCustom("timestamptz").NotNullable();

            Create.Index("ux_custom_field_templates_scope_name")
                .OnTable("custom_field_templates")
                .OnColumn("client_id").Ascending()
                .OnColumn("department_id").Ascending()
                .OnColumn("name").Ascending()
                .WithOptions().Unique();

            Create.Index("ix_custom_field_templates_active_order")
                .OnTable("custom_field_templates")
                .OnColumn("is_active").Ascending()
                .OnColumn("sort_order").Ascending();

            Create.Index("ix_custom_field_templates_department")
                .OnTable("custom_field_templates")
                .OnColumn("department_id").Ascending();
        }

        if (Schema.Table("custom_field_definitions").Exists()
            && !Schema.Table("custom_field_definitions").Column("input_mask").Exists())
        {
            Alter.Table("custom_field_definitions")
                .AddColumn("input_mask").AsString(100).Nullable();
        }

        if (Schema.Table("tickets").Exists()
            && !Schema.Table("tickets").Column("submission_snapshot_md").Exists())
        {
            Alter.Table("tickets")
                .AddColumn("submission_snapshot_md").AsCustom("text").Nullable();
        }

        // Catálogo built-in idempotente (confia no índice único por nome/escopo).
        Execute.Sql(@"
INSERT INTO custom_field_templates
  (id, client_id, department_id, name, label, description, data_type, options_json, validation_regex, input_mask, min_length, max_length, min_value, max_value, default_is_required, is_built_in, is_active, sort_order, created_at, updated_at)
VALUES
  ('c0000000-0000-0000-0000-000000000001', NULL, NULL, 'texto-curto', 'Texto curto', 'Campo de texto simples de linha única.', 0, NULL, NULL, NULL, NULL, NULL, NULL, NULL, false, true, true, 10, now(), now()),
  ('c0000000-0000-0000-0000-000000000002', NULL, NULL, 'texto-longo', 'Texto longo', 'Campo de texto multilinha para descrições.', 0, NULL, NULL, NULL, NULL, NULL, NULL, NULL, false, true, true, 20, now(), now()),
  ('c0000000-0000-0000-0000-000000000003', NULL, NULL, 'numero-inteiro', 'Número inteiro', 'Número inteiro (sem casas decimais).', 1, NULL, NULL, NULL, NULL, NULL, NULL, NULL, false, true, true, 30, now(), now()),
  ('c0000000-0000-0000-0000-000000000004', NULL, NULL, 'decimal', 'Decimal', 'Número decimal.', 2, NULL, NULL, NULL, NULL, NULL, NULL, NULL, false, true, true, 40, now(), now()),
  ('c0000000-0000-0000-0000-000000000005', NULL, NULL, 'valor-brl', 'Valor (R$)', 'Valor monetário em reais, sem negativos.', 2, NULL, NULL, NULL, NULL, NULL, 0, NULL, false, true, true, 50, now(), now()),
  ('c0000000-0000-0000-0000-000000000006', NULL, NULL, 'data', 'Data', 'Data (sem hora).', 4, NULL, NULL, NULL, NULL, NULL, NULL, NULL, false, true, true, 60, now(), now()),
  ('c0000000-0000-0000-0000-000000000007', NULL, NULL, 'data-hora', 'Data e hora', 'Data com hora.', 5, NULL, NULL, NULL, NULL, NULL, NULL, NULL, false, true, true, 70, now(), now()),
  ('c0000000-0000-0000-0000-000000000008', NULL, NULL, 'sim-nao', 'Sim/Não', 'Booleano (Sim ou Não).', 3, NULL, NULL, NULL, NULL, NULL, NULL, NULL, false, true, true, 80, now(), now()),
  ('c0000000-0000-0000-0000-000000000009', NULL, NULL, 'lista-opcoes', 'Lista de opções', 'Escolha única entre opções cadastradas.', 6, NULL, NULL, NULL, NULL, NULL, NULL, NULL, false, true, true, 90, now(), now()),
  ('c0000000-0000-0000-0000-00000000000a', NULL, NULL, 'multipla-escolha', 'Múltipla escolha', 'Múltipla escolha entre opções cadastradas.', 7, NULL, NULL, NULL, NULL, NULL, NULL, NULL, false, true, true, 100, now(), now()),
  ('c0000000-0000-0000-0000-00000000000b', NULL, NULL, 'cpf', 'CPF', 'CPF com 11 dígitos (sem pontuação no valor salvo).', 0, NULL, '^\d{11}$', '999.999.999-99', NULL, NULL, NULL, NULL, false, true, true, 110, now(), now()),
  ('c0000000-0000-0000-0000-00000000000c', NULL, NULL, 'cnpj', 'CNPJ', 'CNPJ com 14 dígitos (sem pontuação no valor salvo).', 0, NULL, '^\d{14}$', '99.999.999/9999-99', NULL, NULL, NULL, NULL, false, true, true, 120, now(), now()),
  ('c0000000-0000-0000-0000-00000000000d', NULL, NULL, 'email', 'E-mail', 'Endereço de e-mail.', 0, NULL, '^[^\s@]+@[^\s@]+\.[^\s@]+$', NULL, NULL, NULL, NULL, NULL, false, true, true, 130, now(), now()),
  ('c0000000-0000-0000-0000-00000000000e', NULL, NULL, 'telefone-br', 'Telefone BR', 'Telefone brasileiro com DDD.', 0, NULL, '^\(?\d{2}\)?\s?\d{4,5}-?\d{4}$', '(99) 99999-9999', NULL, NULL, NULL, NULL, false, true, true, 140, now(), now()),
  ('c0000000-0000-0000-0000-00000000000f', NULL, NULL, 'cep', 'CEP', 'CEP brasileiro.', 0, NULL, '^\d{5}-?\d{3}$', '99999-999', NULL, NULL, NULL, NULL, false, true, true, 150, now(), now()),
  ('c0000000-0000-0000-0000-000000000010', NULL, NULL, 'url', 'URL', 'Endereço web http(s).', 0, NULL, '^https?://[^\s]+$', NULL, NULL, NULL, NULL, NULL, false, true, true, 160, now(), now()),
  ('c0000000-0000-0000-0000-000000000011', NULL, NULL, 'codigo-slug', 'Código/Slug', 'Código alfanumérico com hífen e underline.', 0, NULL, '^[A-Za-z0-9_-]+$', NULL, NULL, NULL, NULL, NULL, false, true, true, 170, now(), now())
ON CONFLICT DO NOTHING;
");
    }

    public override void Down()
    {
        if (Schema.Table("tickets").Exists()
            && Schema.Table("tickets").Column("submission_snapshot_md").Exists())
        {
            Delete.Column("submission_snapshot_md").FromTable("tickets");
        }

        if (Schema.Table("custom_field_definitions").Exists()
            && Schema.Table("custom_field_definitions").Column("input_mask").Exists())
        {
            Delete.Column("input_mask").FromTable("custom_field_definitions");
        }

        if (Schema.Table("custom_field_templates").Exists())
            Delete.Table("custom_field_templates");
    }
}
