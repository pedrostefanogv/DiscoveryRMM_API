using Discovery.Migrations.Migrations;

namespace Discovery.Tests;

/// <summary>
/// B4c — mapeamento PURO do backfill da M229 (base -> override).
///
/// O UPDATE real roda no MigrateUp e exige Postgres; aqui verificamos o
/// contrato do mapeamento extraído: a estratégia de auto-atribuição entra no
/// backfill e o valor é copiado exatamente, preservando o comportamento
/// anterior de todas as linhas.
/// </summary>
public class M229BackfillMappingTests
{
    [Test]
    public void Backfill_copia_assignment_strategy_para_o_override()
    {
        Assert.That(M229BackfillMapping.MapAssignmentStrategy(0), Is.EqualTo(0));
        Assert.That(M229BackfillMapping.MapAssignmentStrategy(1), Is.EqualTo(1));
        Assert.That(M229BackfillMapping.MapAssignmentStrategy(3), Is.EqualTo(3));
        Assert.That(M229BackfillMapping.MapAssignmentStrategy(null), Is.Null);
    }

    [Test]
    public void Backfill_inclui_os_17_pares_base_override()
    {
        Assert.That(M229BackfillMapping.Columns, Has.Count.EqualTo(17));
        Assert.That(M229BackfillMapping.Columns,
            Does.Contain(("assignment_strategy", "assignment_strategy_override")));
    }

    [Test]
    public void Sql_do_backfill_mapeia_assignment_strategy_e_os_demais_campos()
    {
        var sql = M229BackfillMapping.BuildUpdateSql("departments");

        Assert.That(sql, Does.Contain("UPDATE departments"));
        Assert.That(sql, Does.Contain("assignment_strategy_override = assignment_strategy"));
        Assert.That(sql, Does.Contain("ai_assignment_mode_override = ai_assignment_mode"));
        Assert.That(sql, Does.Contain("ai_weight_max_override = ai_weight_max"));

        // Regressão: base e override nunca podem ser invertidos.
        Assert.That(sql, Does.Not.Contain("assignment_strategy = assignment_strategy_override"));
    }
}
