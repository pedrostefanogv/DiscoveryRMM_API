using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Atualiza o catálogo embutido de modelos de campo:
/// - CNPJ passa a ser ALFANUMÉRICO (novo formato da Receita: 12 caracteres
///   alfanuméricos + 2 dígitos verificadores), com máscara "**.***.***/****-99";
/// - Valor (R$) ganha máscara monetária pt-BR ("R$ 9.999.999,99") — antes o
///   modelo não trazia máscara nenhuma.
///
/// Somente linhas embutidas globais (is_built_in = true, sem cliente/departamento):
/// templates personalizados criados pelos usuários não são tocados.
/// </summary>
[Migration(20260928_171)]
public class M171_UpdateCnpjAndCurrencyTemplates : Migration
{
    public override void Up()
    {
        if (!Schema.Table("custom_field_templates").Exists()) return;

        Execute.Sql(@"
            UPDATE custom_field_templates
               SET input_mask = '**.***.***/****-99',
                   validation_regex = '^[A-Za-z0-9]{12}[0-9]{2}$',
                   description = 'CNPJ alfanumérico: 12 caracteres (letras e números) + 2 dígitos verificadores. Sem pontuação no valor salvo.',
                   updated_at = now()
             WHERE name = 'cnpj'
               AND is_built_in = true
               AND client_id IS NULL
               AND department_id IS NULL");

        Execute.Sql(@"
            UPDATE custom_field_templates
               SET input_mask = 'R$ 9.999.999,99',
                   description = 'Valor monetário em reais (pt-BR), sem negativos. A pontuação é apenas visual: o valor é salvo como número.',
                   updated_at = now()
             WHERE name = 'valor-brl'
               AND is_built_in = true
               AND client_id IS NULL
               AND department_id IS NULL");
    }

    public override void Down()
    {
        if (!Schema.Table("custom_field_templates").Exists()) return;

        Execute.Sql(@"
            UPDATE custom_field_templates
               SET input_mask = '99.999.999/9999-99',
                   validation_regex = '^\d{14}$',
                   description = 'CNPJ com 14 dígitos (sem pontuação no valor salvo).',
                   updated_at = now()
             WHERE name = 'cnpj'
               AND is_built_in = true
               AND client_id IS NULL
               AND department_id IS NULL");

        Execute.Sql(@"
            UPDATE custom_field_templates
               SET input_mask = NULL,
                   description = 'Valor monetário em reais, sem negativos.',
                   updated_at = now()
             WHERE name = 'valor-brl'
               AND is_built_in = true
               AND client_id IS NULL
               AND department_id IS NULL");
    }
}
