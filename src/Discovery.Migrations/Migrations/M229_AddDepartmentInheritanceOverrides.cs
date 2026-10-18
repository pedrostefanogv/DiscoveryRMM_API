using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Herança de configuração de triagem por departamento (campo a campo).
///
/// Adiciona as colunas *_override nullable: quando NULL, o departamento herda o
/// valor do departamento GLOBAL pai (um salto); quando preenchida, o próprio
/// valor vence. As colunas antigas continuam sendo o valor-base/legado.
///
/// BACKFILL: copia o valor atual de cada coluna base para o override
/// correspondente em TODAS as linhas existentes. Isso preserva 100% do
/// comportamento anterior — departamentos que já existiam passam a "falar por
/// si"; somente departamentos criados depois da migration nascem com override
/// NULL e herdam por padrão.
///
/// Também cria o índice em inherit_from_global_id usado pela validação de
/// filhos ativos (D6) e pela leitura da equipe efetiva (D7).
///
/// ATENÇÃO: esta migração NÃO foi aplicada em nenhum banco.
/// </summary>
[Migration(20261219_229)]
public class M229_AddDepartmentInheritanceOverrides : Migration
{
    private const string Table = "departments";
    private const string InheritIndex = "ix_departments_inherit_from_global_id";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        // 17º campo: a estratégia de auto-atribuição também passa a herdar.
        AddColumnIfMissing("assignment_strategy_override", "int");
        AddColumnIfMissing("ai_assignment_mode_override", "int");
        AddColumnIfMissing("ai_assignment_min_confidence_override", "numeric(4,3)");
        AddColumnIfMissing("ai_assignment_fallback_strategy_override", "int");
        AddColumnIfMissing("ai_assignment_max_candidates_override", "int");
        AddColumnIfMissing("ai_assignment_weights_json_override", "jsonb");
        AddColumnIfMissing("ai_assignment_instructions_override", "text");
        AddColumnIfMissing("ai_assignment_use_affinity_override", "boolean");
        AddColumnIfMissing("ai_assignment_max_output_tokens_override", "int");

        // Os 8 campos de aprendizado (competências e pesos) também passam a ter
        // override próprio, com o mesmo contrato (NULL = herda do global pai).
        AddColumnIfMissing("ai_skill_learning_mode_override", "int");
        AddColumnIfMissing("ai_skill_min_evidence_override", "int");
        AddColumnIfMissing("ai_skill_max_tags_override", "int");
        AddColumnIfMissing("ai_weight_learning_mode_override", "int");
        AddColumnIfMissing("ai_weight_max_delta_per_cycle_override", "numeric(4,3)");
        AddColumnIfMissing("ai_weight_cycle_days_override", "int");
        AddColumnIfMissing("ai_weight_min_override", "numeric(4,3)");
        AddColumnIfMissing("ai_weight_max_override", "numeric(4,3)");

        // Backfill: preserva o comportamento atual de TODAS as linhas existentes,
        // nos 17 campos (estratégia + 8 de atribuição + 8 de aprendizado).
        // O SQL é gerado pelo mapeamento puro (testável sem Postgres).
        Execute.Sql(M229BackfillMapping.BuildUpdateSql(Table));

        if (!Schema.Table(Table).Index(InheritIndex).Exists())
        {
            Create.Index(InheritIndex)
                .OnTable(Table)
                .OnColumn("inherit_from_global_id").Ascending();
        }
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Index(InheritIndex).Exists())
            Delete.Index(InheritIndex).OnTable(Table);

        foreach (var column in new[]
                 {
                     "ai_weight_max_override",
                     "ai_weight_min_override",
                     "ai_weight_cycle_days_override",
                     "ai_weight_max_delta_per_cycle_override",
                     "ai_weight_learning_mode_override",
                     "ai_skill_max_tags_override",
                     "ai_skill_min_evidence_override",
                     "ai_skill_learning_mode_override",
                     "ai_assignment_max_output_tokens_override",
                     "ai_assignment_use_affinity_override",
                     "ai_assignment_instructions_override",
                     "ai_assignment_weights_json_override",
                     "ai_assignment_max_candidates_override",
                     "ai_assignment_fallback_strategy_override",
                     "ai_assignment_min_confidence_override",
                     "ai_assignment_mode_override",
                     "assignment_strategy_override"
                 })
        {
            if (Schema.Table(Table).Column(column).Exists())
                Delete.Column(column).FromTable(Table);
        }
    }

    private void AddColumnIfMissing(string column, string sqlType)
    {
        if (Schema.Table(Table).Column(column).Exists())
            return;

        Alter.Table(Table).AddColumn(column).AsCustom(sqlType).Nullable();
    }
}

/// <summary>
/// Mapeamento PURO do backfill da M229: cada coluna base é copiada para o seu
/// *_override nullable, preservando o comportamento anterior. Fica separado da
/// migration para ser testável sem um Postgres (a execução real do UPDATE exige
/// Postgres e roda no MigrateUp).
/// </summary>
public static class M229BackfillMapping
{
    /// <summary>Pares (coluna base, coluna override) copiados no backfill.</summary>
    public static readonly IReadOnlyList<(string BaseColumn, string OverrideColumn)> Columns =
    [
        ("assignment_strategy", "assignment_strategy_override"),
        ("ai_assignment_mode", "ai_assignment_mode_override"),
        ("ai_assignment_min_confidence", "ai_assignment_min_confidence_override"),
        ("ai_assignment_fallback_strategy", "ai_assignment_fallback_strategy_override"),
        ("ai_assignment_max_candidates", "ai_assignment_max_candidates_override"),
        ("ai_assignment_weights_json", "ai_assignment_weights_json_override"),
        ("ai_assignment_instructions", "ai_assignment_instructions_override"),
        ("ai_assignment_use_affinity", "ai_assignment_use_affinity_override"),
        ("ai_assignment_max_output_tokens", "ai_assignment_max_output_tokens_override"),
        ("ai_skill_learning_mode", "ai_skill_learning_mode_override"),
        ("ai_skill_min_evidence", "ai_skill_min_evidence_override"),
        ("ai_skill_max_tags", "ai_skill_max_tags_override"),
        ("ai_weight_learning_mode", "ai_weight_learning_mode_override"),
        ("ai_weight_max_delta_per_cycle", "ai_weight_max_delta_per_cycle_override"),
        ("ai_weight_cycle_days", "ai_weight_cycle_days_override"),
        ("ai_weight_min", "ai_weight_min_override"),
        ("ai_weight_max", "ai_weight_max_override")
    ];

    /// <summary>Cópia pura base -> override (o valor é o mesmo; null continua null).</summary>
    public static int? MapAssignmentStrategy(int? baseValue) => baseValue;

    /// <summary>Monta o UPDATE único do backfill para a tabela informada.</summary>
    public static string BuildUpdateSql(string table)
    {
        var assignments = string.Join(",\n                   ",
            Columns.Select(column => $"{column.OverrideColumn} = {column.BaseColumn}"));

        return $@"
            UPDATE {table}
               SET {assignments};";
    }
}
