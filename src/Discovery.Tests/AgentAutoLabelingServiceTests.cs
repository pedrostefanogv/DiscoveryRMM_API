using System.Diagnostics.Metrics;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Testes de integracao do motor de auto-labeling contra um DbContext InMemory real.
///
/// Diferente dos testes com stubs, estes exercitam o caminho completo
/// (avaliacao -> matches -> labels efetivas) e cobrem regressoes que so aparecem
/// quando o estado e persistido, como o caso em que uma label deveria ser removida
/// na MESMA execucao em que o agente deixa de casar com a regra.
/// </summary>
public class AgentAutoLabelingServiceTests
{
    [Test]
    public async Task Evaluate_WhenRuleMatches_AddsAutomaticLabelAndMatch()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "test");

        var labels = await fx.Db.AgentLabels.AsNoTracking().ToListAsync();
        Assert.That(labels, Has.Count.EqualTo(1));
        Assert.That(labels[0].Label, Is.EqualTo("PROD"));
        Assert.That(labels[0].SourceType, Is.EqualTo(AgentLabelSourceType.Automatic));

        var matches = await fx.Db.AgentLabelRuleMatches.AsNoTracking().ToListAsync();
        Assert.That(matches, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Evaluate_WhenAgentStopsMatching_RemovesLabelInSameRun()
    {
        // Regressao: a derivacao das labels passou a usar o dicionario em memoria de
        // matches. Remover de _db nao remove do dicionario, entao a label sobrevivia
        // a execucao e so saia na reconciliacao seguinte.
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1), "pre-condicao: label aplicada");

        // O agente deixa de casar com a regra.
        var agent = await fx.Db.Agents.SingleAsync(a => a.Id == fx.AgentId);
        agent.Hostname = "WORKSTATION-99";
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "second");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(0),
            "A label deve ser removida na MESMA execucao em que o match deixa de existir.");
        Assert.That(await fx.Db.AgentLabelRuleMatches.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task Evaluate_WhenRuleDisabled_RemovesLabel()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1));

        var rule = await fx.Db.AgentLabelRules.SingleAsync();
        rule.IsEnabled = false;
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "after-disable");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(0),
            "Desabilitar a regra deve remover a label automatica.");
    }

    [Test]
    public async Task Evaluate_WhenApplyOnlyMode_KeepsLabelAfterAgentStopsMatching()
    {
        // Semantica de ApplyOnly: nao remover. Documenta explicitamente o comportamento.
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyOnly);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");

        var agent = await fx.Db.Agents.SingleAsync(a => a.Id == fx.AgentId);
        agent.Hostname = "WORKSTATION-99";
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "second");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1),
            "ApplyOnly preserva a label mesmo quando o agente deixa de casar.");
    }

    [Test]
    public async Task Evaluate_UpdatesLastEvaluatedAt_EvenWhenLabelUnchanged()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        var first = (await fx.Db.AgentLabelRuleMatches.AsNoTracking().SingleAsync()).LastEvaluatedAt;

        await Task.Delay(15);
        await fx.Service.EvaluateAgentAsync(fx.AgentId, "second");
        var second = (await fx.Db.AgentLabelRuleMatches.AsNoTracking().SingleAsync()).LastEvaluatedAt;

        Assert.That(second, Is.GreaterThan(first),
            "LastEvaluatedAt deve refletir toda avaliacao bem-sucedida, nao so quando a label muda.");
    }

    [Test]
    public async Task Evaluate_RecordsAuditTrail()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        var logs = await fx.Db.AgentLabelChangeLogs.AsNoTracking().ToListAsync();

        Assert.That(logs, Has.Count.EqualTo(1));
        Assert.That(logs[0].Action, Is.EqualTo("Added"));
        Assert.That(logs[0].Label, Is.EqualTo("PROD"));
        Assert.That(logs[0].Reason, Is.EqualTo("first"));
        Assert.That(logs[0].RuleId, Is.EqualTo(fx.RuleId),
            "O historico precisa atribuir QUAL regra aplicou a label (diagnostico de oscilacoes).");

        // O DTO consultado pela UI resolve o nome da regra.
        var history = await fx.LabelRepository.GetChangeLogAsync(fx.AgentId, 10);
        Assert.That(history[0].RuleName, Is.EqualTo("Servidores"));

        var agent = await fx.Db.Agents.SingleAsync(a => a.Id == fx.AgentId);
        agent.Hostname = "WORKSTATION-99";
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "second");
        logs = await fx.Db.AgentLabelChangeLogs.AsNoTracking().OrderBy(l => l.OccurredAt).ToListAsync();

        Assert.That(logs, Has.Count.EqualTo(2));
        Assert.That(logs[1].Action, Is.EqualTo("Removed"));
    }

    [Test]
    public async Task Evaluate_SoftwareNotContains_UsesUniversalSemantics()
    {
        // Regressao: a negacao em colecao usava software.Any(item => !contains), que e
        // verdadeiro para praticamente qualquer maquina com mais de um software — uma
        // regra "nao tem Chrome" marcava a frota inteira.
        var expression = new Core.DTOs.AgentLabelRuleExpressionNodeDto
        {
            NodeType = AgentLabelNodeType.Group,
            LogicalOperator = AgentLabelLogicalOperator.And,
            Children =
            [
                new Core.DTOs.AgentLabelRuleExpressionNodeDto
                {
                    NodeType = AgentLabelNodeType.Condition,
                    Field = AgentLabelField.SoftwareName,
                    Operator = AgentLabelComparisonOperator.NotContains,
                    Value = "Chrome"
                }
            ]
        };

        await using var withChrome = await Fixture.CreateAsync(
            hostname: "SRV-01",
            applyMode: AgentLabelApplyMode.ApplyAndRemove,
            expression: expression,
            installedSoftware: ["Google Chrome", "7-Zip"]);

        await withChrome.Service.EvaluateAgentAsync(withChrome.AgentId, "test");
        Assert.That(await withChrome.Db.AgentLabels.CountAsync(), Is.EqualTo(0),
            "Com Chrome instalado, 'Nome nao contem Chrome' nao pode dar match.");

        await using var withoutChrome = await Fixture.CreateAsync(
            hostname: "SRV-02",
            applyMode: AgentLabelApplyMode.ApplyAndRemove,
            expression: expression,
            installedSoftware: ["7-Zip"]);

        await withoutChrome.Service.EvaluateAgentAsync(withoutChrome.AgentId, "test");
        Assert.That(await withoutChrome.Db.AgentLabels.CountAsync(), Is.EqualTo(1),
            "Sem Chrome, 'Nome nao contem Chrome' deve dar match.");
    }

    [Test]
    public async Task DryRun_WhenNoMatch_DescribesFailedConditions()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "WORKSTATION-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        var response = await fx.Service.DryRunAsync(new Core.DTOs.AgentLabelRuleDryRunRequest
        {
            AgentId = fx.AgentId,
            Label = "PROD",
            ApplyMode = AgentLabelApplyMode.ApplyAndRemove,
            Expression = Fixture.HostnameContainsSrvExpression()
        });

        Assert.That(response.Matched, Is.False);
        Assert.That(response.FailedConditions, Is.Not.Empty, "Dry-run explicado deve descrever a condicao falsa.");
        Assert.That(response.FailedConditions[0], Does.Contain("Hostname"));
    }

    [Test]
    public async Task Evaluate_RecordsActorInChangeLog()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "manual-reprocess", "user:1234");

        var log = await fx.Db.AgentLabelChangeLogs.AsNoTracking().SingleAsync();
        Assert.That(log.Actor, Is.EqualTo("user:1234"),
            "A auditoria deve registrar o autor da acao (antes Actor ficava sempre nulo).");
    }

    [Test]
    public async Task ReprocessAllAgents_AppliesRuleToEveryAgent()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        // Um segundo agente que nao casa com a regra.
        fx.Db.Agents.Add(new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = fx.SiteId,
            Hostname = "WORKSTATION-77",
            Status = AgentStatus.Online,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await fx.Db.SaveChangesAsync();

        await fx.Service.ReprocessAllAgentsAsync("bulk");

        var labels = await fx.Db.AgentLabels.AsNoTracking().ToListAsync();
        Assert.That(labels, Has.Count.EqualTo(1), "Apenas o agente com match deve receber a label.");
        Assert.That(labels[0].AgentId, Is.EqualTo(fx.AgentId));
    }

    [Test]
    public async Task EvaluateImpact_EstimatesMatchRateFromSample()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        fx.Db.Agents.Add(new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = fx.SiteId,
            Hostname = "WORKSTATION-77",
            Status = AgentStatus.Online,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await fx.Db.SaveChangesAsync();

        var impact = await fx.Service.EvaluateImpactAsync(new Core.DTOs.AgentLabelRuleImpactRequest
        {
            Label = "PROD",
            ApplyMode = AgentLabelApplyMode.ApplyAndRemove,
            Expression = Fixture.HostnameContainsSrvExpression(),
            SampleSize = 100
        });

        Assert.That(impact.Sampled, Is.EqualTo(2));
        Assert.That(impact.Matched, Is.EqualTo(1));
        Assert.That(impact.EstimatedTotalAgents, Is.EqualTo(2));
        Assert.That(impact.WouldAddLabel, Is.EqualTo(1));
    }

    [Test]
    public async Task Evaluate_WhenManualLabelHasSameName_DoesNotViolateUniqueIndex()
    {
        // Regressao: o indice unico e (agent_id, label) ignorando a origem. Uma label
        // manual "PROD" + uma regra que gera "PROD" fazia o SaveChanges do lote inteiro
        // falhar com DbUpdateException.
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        fx.Db.AgentLabels.Add(new AgentLabel
        {
            Id = Guid.NewGuid(),
            AgentId = fx.AgentId,
            Label = "PROD",
            SourceType = AgentLabelSourceType.Manual,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await fx.Db.SaveChangesAsync();

        await Assert.DoesNotThrowAsync(() => fx.Service.EvaluateAgentAsync(fx.AgentId, "collision"));

        var labels = await fx.Db.AgentLabels.AsNoTracking().ToListAsync();
        Assert.That(labels, Has.Count.EqualTo(1), "Nao deve duplicar a label ja existente.");
        Assert.That(labels[0].SourceType, Is.EqualTo(AgentLabelSourceType.Manual));
    }

    [Test]
    public async Task Evaluate_WhenAutomaticLabelManuallyRemoved_DoesNotRecreateIt()
    {
        // Sem a supressao, o reconcile recriava a label logo apos o usuario remove-la.
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        var label = await fx.Db.AgentLabels.SingleAsync();
        Assert.That(label.Label, Is.EqualTo("PROD"));

        // Usuario remove manualmente a label automatica (com supressao).
        await fx.LabelRepository.SuppressAutomaticLabelAsync(fx.AgentId, "PROD", "tester");
        await fx.LabelRepository.DeleteAsync(label.Id);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "after-manual-removal");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(0),
            "A label removida manualmente nao deve ser recriada pelo reconcile.");
    }

    [Test]
    public async Task Evaluate_WhenRuleStopsMatching_ClearsSuppressionSoFutureMatchReapplies()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        var label = await fx.Db.AgentLabels.SingleAsync();
        await fx.LabelRepository.SuppressAutomaticLabelAsync(fx.AgentId, "PROD", "tester");
        await fx.LabelRepository.DeleteAsync(label.Id);

        // Agente deixa de casar com a regra.
        var agent = await fx.Db.Agents.SingleAsync(a => a.Id == fx.AgentId);
        agent.Hostname = "WORKSTATION-99";
        await fx.Db.SaveChangesAsync();
        await fx.Service.EvaluateAgentAsync(fx.AgentId, "no-match");

        Assert.That(await fx.Db.AgentLabelSuppressions.CountAsync(), Is.EqualTo(0),
            "A supressao deve ser limpa quando a regra deixa de casar.");

        // Agora volta a casar: a label deve ser reaplicada.
        agent.Hostname = "SRV-PROD-02";
        await fx.Db.SaveChangesAsync();
        await fx.Service.EvaluateAgentAsync(fx.AgentId, "match-again");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1),
            "Um novo match deve reaplicar a label.");
    }

    [Test]
    public async Task EvaluateImpact_SampleIsRepresentativeAcrossSites()
    {
        // Amostragem estratificada: um cliente grande nao deve dominar a amostra.
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        var now = DateTime.UtcNow;
        // 30 agentes "SRV" no site A (o original) + 30 "WORKSTATION" em um site novo.
        var otherSite = Guid.NewGuid();
        for (var i = 0; i < 30; i++)
        {
            fx.Db.Agents.Add(new Agent
            {
                Id = Guid.NewGuid(),
                SiteId = fx.SiteId,
                Hostname = "SRV-" + i,
                Status = AgentStatus.Online,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        for (var i = 0; i < 30; i++)
        {
            fx.Db.Agents.Add(new Agent
            {
                Id = Guid.NewGuid(),
                SiteId = otherSite,
                Hostname = "WORKSTATION-" + i,
                Status = AgentStatus.Online,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        await fx.Db.SaveChangesAsync();

        var impact = await fx.Service.EvaluateImpactAsync(new Core.DTOs.AgentLabelRuleImpactRequest
        {
            Label = "PROD",
            ApplyMode = AgentLabelApplyMode.ApplyAndRemove,
            Expression = Fixture.HostnameContainsSrvExpression(),
            SampleSize = 20
        });

        Assert.That(impact.EstimatedTotalAgents, Is.EqualTo(61));
        Assert.That(impact.Sampled, Is.LessThanOrEqualTo(20));

        // Com amostragem estratificada, ambos os estratos entram na amostra, entao o
        // match nao pode ser 0 nem a amostra inteira (que era o vies do "primeiros N").
        Assert.That(impact.Matched, Is.GreaterThan(0), "O estrato com match deve aparecer na amostra.");
        Assert.That(impact.Matched, Is.LessThan(impact.Sampled), "O estrato sem match tambem deve aparecer.");
    }
    [Test]
    public async Task Evaluate_WhenLabelReaddedManually_ClearsStaleSuppression()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        var label = await fx.Db.AgentLabels.SingleAsync();
        await fx.LabelRepository.SuppressAutomaticLabelAsync(fx.AgentId, "PROD", "tester");
        await fx.LabelRepository.DeleteAsync(label.Id);
        Assert.That(await fx.Db.AgentLabelSuppressions.CountAsync(), Is.EqualTo(1));

        // Usuario readiciona a label na mao.
        await fx.LabelRepository.ClearSuppressionAsync(fx.AgentId, "PROD");
        await fx.LabelRepository.AddAsync(new AgentLabel
        {
            Id = Guid.NewGuid(),
            AgentId = fx.AgentId,
            Label = "PROD",
            SourceType = AgentLabelSourceType.Manual,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        Assert.That(await fx.Db.AgentLabelSuppressions.CountAsync(), Is.EqualTo(0),
            "Readicionar a label deve limpar a supressao orfa.");
    }
    [Test]
    public async Task Evaluate_DiskFullScenario_LabelReappliesWhenConditionReturns_ApplyAndRemove()
    {
        // Cenario do usuario: "HD cheio" aplica a label, o usuario remove, o HD e
        // liberado e depois enche DE NOVO -> a label deve voltar.
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "disk-full-1");
        var label = await fx.Db.AgentLabels.SingleAsync();
        Assert.That(label.Label, Is.EqualTo("PROD"), "1) condicao verdadeira aplica a label");

        // Usuario remove manualmente (supressao).
        await fx.LabelRepository.SuppressAutomaticLabelAsync(fx.AgentId, "PROD", "tester");
        await fx.LabelRepository.DeleteAsync(label.Id);

        // HD liberado: a condicao deixa de ser verdadeira.
        var agent = await fx.Db.Agents.SingleAsync(a => a.Id == fx.AgentId);
        agent.Hostname = "WORKSTATION-99";
        await fx.Db.SaveChangesAsync();
        await fx.Service.EvaluateAgentAsync(fx.AgentId, "disk-freed");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(0), "2) sem match, sem label");
        Assert.That(await fx.Db.AgentLabelSuppressions.CountAsync(), Is.EqualTo(0),
            "3) a supressao deve ser liberada quando a condicao deixa de valer");

        // HD enche NOVAMENTE.
        agent.Hostname = "SRV-PROD-02";
        await fx.Db.SaveChangesAsync();
        await fx.Service.EvaluateAgentAsync(fx.AgentId, "disk-full-2");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1),
            "4) a label deve voltar a ser aplicada quando a condicao retorna");
    }
    [Test]
    public async Task Evaluate_DiskFullScenario_ApplyOnly_LabelReappliesWhenConditionReturns()
    {
        // Em ApplyOnly o match NUNCA e removido. Se a supressao so fosse liberada
        // quando a condicao deixa de valer, a label nunca voltaria — contrariando
        // o cenario do usuario.
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyOnly);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "disk-full-1");
        var label = await fx.Db.AgentLabels.SingleAsync();

        // Usuario remove manualmente.
        await fx.LabelRepository.SuppressAutomaticLabelAsync(fx.AgentId, "PROD", "tester");
        await fx.LabelRepository.DeleteAsync(label.Id);

        // HD liberado.
        var agent = await fx.Db.Agents.SingleAsync(a => a.Id == fx.AgentId);
        agent.Hostname = "WORKSTATION-99";
        await fx.Db.SaveChangesAsync();
        await fx.Service.EvaluateAgentAsync(fx.AgentId, "disk-freed");

        // HD enche novamente.
        agent.Hostname = "SRV-PROD-02";
        await fx.Db.SaveChangesAsync();
        await fx.Service.EvaluateAgentAsync(fx.AgentId, "disk-full-2");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1),
            "ApplyOnly: a label deve voltar quando a condicao retorna");
    }
    [Test]
    public async Task Suppressions_AreListedAndReleasable()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        var label = await fx.Db.AgentLabels.SingleAsync();
        await fx.LabelRepository.SuppressAutomaticLabelAsync(fx.AgentId, "PROD", "tester");
        await fx.LabelRepository.DeleteAsync(label.Id);

        // A supressao fica visivel, com o nome da regra que produz a label.
        var listed = await fx.LabelRepository.GetSuppressionsByAgentIdAsync(fx.AgentId);
        Assert.That(listed, Has.Count.EqualTo(1));
        Assert.That(listed[0].Label, Is.EqualTo("PROD"));
        Assert.That(listed[0].SuppressedBy, Is.EqualTo("tester"));
        Assert.That(listed[0].RuleName, Is.EqualTo("Servidores"));

        // Liberar a supressao faz a label voltar na proxima avaliacao.
        var released = await fx.LabelRepository.ReleaseSuppressionAsync(listed[0].Id);
        Assert.That(released, Is.EqualTo(fx.AgentId), "Liberar deve devolver o agente afetado.");
        Assert.That(await fx.Db.AgentLabelSuppressions.CountAsync(), Is.EqualTo(0));

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "after-release");
        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1),
            "Liberar a supressao deve permitir a reaplicacao da label.");
    }
    // -------------------------------------------------------------------------
    // Fixture
    // -------------------------------------------------------------------------

    [Test]
    public async Task ReprocessChangedAgents_WhenNothingChanged_SkipsEvaluation()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        // Primeira passagem (sem watermark) avalia o agente.
        var first = new CollectingProgress();
        await fx.Service.ReprocessChangedAgentsAsync("first", progress: first);
        Assert.That(first.Last?.Processed, Is.EqualTo(1));

        // Segunda passagem sem mudancas: nada e reavaliado.
        var second = new CollectingProgress();
        await fx.Service.ReprocessChangedAgentsAsync("second", progress: second);

        Assert.That(second.Last?.Processed, Is.EqualTo(0),
            "Sem mudanca desde o watermark, a reconciliacao incremental nao deve avaliar ninguem.");
        Assert.That(await fx.Db.AgentLabelChangeLogs.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task ReprocessChangedAgents_WhenAgentChanged_EvaluatesOnlyIt()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);
        await fx.Service.ReprocessChangedAgentsAsync("first");
        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1), "pre-condicao: label aplicada");

        // O agente muda (hostname + UpdatedAt) depois do watermark.
        var agent = await fx.Db.Agents.SingleAsync(a => a.Id == fx.AgentId);
        agent.Hostname = "WORKSTATION-99";
        agent.UpdatedAt = DateTime.UtcNow.AddMinutes(1);
        await fx.Db.SaveChangesAsync();

        var progress = new CollectingProgress();
        await fx.Service.ReprocessChangedAgentsAsync("second", progress: progress);

        Assert.That(progress.Last?.Processed, Is.EqualTo(1), "O agente alterado deve ser reavaliado.");
        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(0),
            "O agente deixou de casar e a label deve sair ja na passagem incremental.");
    }

    [Test]
    public async Task ReprocessChangedAgents_WhenSoftwareInventoryChanged_EvaluatesIt()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "WORKSTATION-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        // Watermark inicial (nada instalado -> nenhum match).
        await fx.Service.ReprocessChangedAgentsAsync("first");

        // Instala um software DEPOIS do watermark (LastSeenAt/UpdatedAt futuros).
        var now = DateTime.UtcNow.AddMinutes(1);
        var catalog = new SoftwareCatalog
        {
            Id = Guid.NewGuid(),
            Name = "Google Chrome",
            Publisher = "Google",
            Fingerprint = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now
        };

        fx.Db.SoftwareCatalogs.Add(catalog);
        fx.Db.AgentSoftwareInventories.Add(new AgentSoftwareInventory
        {
            Id = Guid.NewGuid(),
            AgentId = fx.AgentId,
            SoftwareId = catalog.Id,
            IsPresent = true,
            Version = "1.0.0",
            CollectedAt = now,
            FirstSeenAt = now,
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        await fx.Db.SaveChangesAsync();

        var progress = new CollectingProgress();
        await fx.Service.ReprocessChangedAgentsAsync("second", progress: progress);

        Assert.That(progress.Last?.Processed, Is.EqualTo(1),
            "Mudanca no inventario de software deve colocar o agente na passagem incremental.");
    }

    [Test]
    public async Task LabelService_RecordsRuleVersionsOnCreateAndUpdate()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        var ruleRepository = new AgentLabelRuleRepository(fx.Db);
        var service = new LabelService(
            new AgentLabelRepository(fx.Db), ruleRepository, fx.Redis, NullLogger<LabelService>.Instance);

        var created = await service.CreateRuleAsync(new AgentLabelRule
        {
            Name = "Versao 1",
            Label = "V1",
            ExpressionJson = "{}",
            CreatedBy = "user:1"
        });

        var versions = await service.GetRuleVersionsAsync(created.Id, 10);
        Assert.That(versions, Has.Count.EqualTo(1), "Criar regra deve gerar um snapshot de configuracao.");
        Assert.That(versions[0].ChangedBy, Is.EqualTo("user:1"));
        Assert.That(versions[0].Name, Is.EqualTo("Versao 1"));

        created.Name = "Versao 2";
        created.UpdatedBy = "user:2";
        await service.UpdateRuleAsync(created);

        versions = await service.GetRuleVersionsAsync(created.Id, 10);
        Assert.That(versions, Has.Count.EqualTo(2), "Cada escrita deve gerar um snapshot.");
        Assert.That(versions[0].Name, Is.EqualTo("Versao 2"), "Mais recente primeiro.");
        Assert.That(versions[0].ChangedBy, Is.EqualTo("user:2"));
    }

    [Test]
    public async Task Evaluate_PublishesLabelingMetrics()
    {
        long added = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == LabelingMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "agent_labeling.labels_added")
                Interlocked.Add(ref added, measurement);
        });
        listener.Start();

        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);
        await fx.Service.EvaluateAgentAsync(fx.AgentId, "metrics");

        Assert.That(added, Is.GreaterThanOrEqualTo(1),
            "Aplicar uma label deve ser observavel em agent_labeling.labels_added.");
    }

    // -------------------------------------------------------------------------
    // Modo Remover (labels manuais)
    // -------------------------------------------------------------------------

    [Test]
    public async Task Remove_Exact_RemovesManualLabelWhenMatched()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.Remove);
        AddManualLabel(fx, "PROD");
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "remove-rule");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(0),
            "A condicao casou e a label manual deve ser removida.");
        Assert.That(await fx.Db.AgentLabelRuleMatches.CountAsync(), Is.EqualTo(1),
            "O match da regra Remove e registrado para exibicao/progresso.");

        var logs = await fx.Db.AgentLabelChangeLogs.AsNoTracking().ToListAsync();
        Assert.That(logs, Has.Count.EqualTo(1));
        Assert.That(logs[0].Action, Is.EqualTo("Removed"));
        Assert.That(logs[0].SourceType, Is.EqualTo(AgentLabelSourceType.Manual));
        Assert.That(logs[0].RuleId, Is.EqualTo(fx.RuleId),
            "A remocao pelo modo Remove deve apontar a regra responsavel.");
    }

    [Test]
    public async Task Remove_Exact_KeepsManualLabelWhenNotMatched()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "WORKSTATION-01", applyMode: AgentLabelApplyMode.Remove);
        AddManualLabel(fx, "PROD");
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "no-match");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1),
            "Condicao falsa nao remove nada.");
    }

    [Test]
    public async Task Remove_TouchesOnlyManualLabels()
    {
        // Regra Remove aponta para TEMP-X; uma regra ADITIVA mantem a automatica PROD.
        await using var fx = await Fixture.CreateAsync(
            hostname: "SRV-PROD-01",
            applyMode: AgentLabelApplyMode.Remove,
            ruleLabel: "TEMP-X");

        fx.Db.AgentLabelRules.Add(new AgentLabelRule
        {
            Id = Guid.NewGuid(),
            Name = "Aditiva",
            Label = "PROD",
            IsEnabled = true,
            ApplyMode = AgentLabelApplyMode.ApplyOnly,
            LabelMatch = AgentLabelLabelMatch.Exact,
            ExpressionJson = System.Text.Json.JsonSerializer.Serialize(
                Fixture.HostnameContainsSrvExpression(),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        fx.Db.AgentLabels.Add(new AgentLabel
        {
            Id = Guid.NewGuid(),
            AgentId = fx.AgentId,
            Label = "PROD",
            SourceType = AgentLabelSourceType.Automatic,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        AddManualLabel(fx, "TEMP-X");
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "sources");

        var remaining = await fx.Db.AgentLabels.AsNoTracking().Select(label => label.Label).ToListAsync();
        Assert.That(remaining, Is.EquivalentTo(new[] { "PROD" }),
            "A automatica mantida pela regra aditiva permanece; a manual alvo da Remove sai.");
    }

    [Test]
    public async Task Remove_Prefix_RemovesAllMatchingManualLabels()
    {
        await using var fx = await Fixture.CreateAsync(
            hostname: "SRV-PROD-01",
            applyMode: AgentLabelApplyMode.Remove,
            ruleLabel: "TEMP-",
            labelMatch: AgentLabelLabelMatch.Prefix);

        AddManualLabel(fx, "TEMP-2026");
        AddManualLabel(fx, "TEMP-LAB");
        AddManualLabel(fx, "PROD");
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "prefix");

        var remaining = await fx.Db.AgentLabels.AsNoTracking().Select(label => label.Label).ToListAsync();
        Assert.That(remaining, Is.EquivalentTo(new[] { "PROD" }),
            "O prefixo TEMP- remove apenas as labels que casam.");
    }

    [Test]
    public async Task Remove_ProtectedLabelIsNotRemoved()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.Remove);
        AddManualLabel(fx, "PROD");

        fx.Db.AgentLabelProtectedLabels.Add(new AgentLabelProtectedLabel
        {
            Id = Guid.NewGuid(),
            Label = "PROD",
            CreatedBy = "user:1",
            CreatedAt = DateTime.UtcNow
        });
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "protected");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1),
            "Whitelist vence o modo Remove (defesa em profundidade).");
    }

    [Test]
    public async Task Remove_AdditiveRuleWins()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.Remove);

        // Regra aditiva habilitada que mantem "PROD" neste agente.
        fx.Db.AgentLabelRules.Add(new AgentLabelRule
        {
            Id = Guid.NewGuid(),
            Name = "Aditiva",
            Label = "PROD",
            IsEnabled = true,
            ApplyMode = AgentLabelApplyMode.ApplyOnly,
            LabelMatch = AgentLabelLabelMatch.Exact,
            ExpressionJson = System.Text.Json.JsonSerializer.Serialize(
                Fixture.HostnameContainsSrvExpression(),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        AddManualLabel(fx, "PROD");
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "additive-wins");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1),
            "Label mantida por regra aditiva nunca e removida pelo modo Remove.");
    }

    [Test]
    public async Task Remove_ReAddedWhileMatched_IsRemovedAgain()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.Remove);
        AddManualLabel(fx, "PROD");
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "first");
        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(0));

        AddManualLabel(fx, "PROD");
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "second");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(0),
            "Enforcement: enquanto a condicao valer, a re-adicao e removida de novo.");
    }

    [Test]
    public async Task DryRun_RemoveMode_ReportsRemovableManualLabels()
    {
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-PROD-01", applyMode: AgentLabelApplyMode.Remove);
        AddManualLabel(fx, "PROD");
        AddManualLabel(fx, "TEMP-1");
        await fx.Db.SaveChangesAsync();

        var response = await fx.Service.DryRunAsync(new Core.DTOs.AgentLabelRuleDryRunRequest
        {
            AgentId = fx.AgentId,
            Label = "PROD",
            ApplyMode = AgentLabelApplyMode.Remove,
            LabelMatch = AgentLabelLabelMatch.Exact,
            Expression = Fixture.HostnameContainsSrvExpression()
        });

        Assert.That(response.Matched, Is.True);
        Assert.That(response.RemovableLabels, Is.EquivalentTo(new[] { "PROD" }));
        Assert.That(response.CurrentManualLabels, Is.EquivalentTo(new[] { "PROD", "TEMP-1" }));
        Assert.That(response.WouldRemoveLabel, Is.True);
        Assert.That(response.WouldAddLabel, Is.False, "Modo Remove nunca adiciona.");
    }

    [Test]
    public async Task Remove_ShortPrefixRule_IsSkippedByDefenseInDepth()
    {
        // Regra com prefixo de 1 caractere nao pode "apagar tudo" nem se chegar ao banco
        // por um caminho que burle a validacao de escrita.
        await using var fx = await Fixture.CreateAsync(
            hostname: "SRV-PROD-01",
            applyMode: AgentLabelApplyMode.Remove,
            ruleLabel: "T",
            labelMatch: AgentLabelLabelMatch.Prefix);

        AddManualLabel(fx, "TEMP-1");
        await fx.Db.SaveChangesAsync();

        await fx.Service.EvaluateAgentAsync(fx.AgentId, "short-prefix");

        Assert.That(await fx.Db.AgentLabels.CountAsync(), Is.EqualTo(1),
            "A regra invalida deve ser ignorada pelo motor (defesa em profundidade).");
    }

    [Test]
    public async Task GetDistinctLabels_WithSourceFilter_ReturnsOnlyThatSource()
    {
        // O seletor de vinculacao manual do agente precisa ver SO as labels manuais.
        await using var fx = await Fixture.CreateAsync(hostname: "SRV-01", applyMode: AgentLabelApplyMode.ApplyAndRemove);

        fx.Db.AgentLabels.AddRange(
            new AgentLabel
            {
                Id = Guid.NewGuid(),
                AgentId = fx.AgentId,
                Label = "PROD",
                SourceType = AgentLabelSourceType.Automatic,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new AgentLabel
            {
                Id = Guid.NewGuid(),
                AgentId = fx.AgentId,
                Label = "Livre",
                SourceType = AgentLabelSourceType.Manual,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        await fx.Db.SaveChangesAsync();

        var repository = new AgentLabelRepository(fx.Db);

        var manual = await repository.GetDistinctLabelsAsync(500, AgentLabelSourceType.Manual);
        Assert.That(manual, Is.EquivalentTo(new[] { "Livre" }));

        var all = await repository.GetDistinctLabelsAsync(500, null);
        Assert.That(all, Is.EquivalentTo(new[] { "Livre", "PROD" }));
    }

    private static void AddManualLabel(Fixture fx, string label)
    {
        fx.Db.AgentLabels.Add(new AgentLabel
        {
            Id = Guid.NewGuid(),
            AgentId = fx.AgentId,
            Label = label,
            SourceType = AgentLabelSourceType.Manual,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
    }

    private sealed class CollectingProgress : IProgress<Core.DTOs.AgentLabelReprocessProgress>
    {
        public List<Core.DTOs.AgentLabelReprocessProgress> Reports { get; } = [];
        public Core.DTOs.AgentLabelReprocessProgress? Last => Reports.Count > 0 ? Reports[^1] : null;
        public void Report(Core.DTOs.AgentLabelReprocessProgress value) => Reports.Add(value);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required DiscoveryDbContext Db { get; init; }
        public required AgentAutoLabelingService Service { get; init; }
        public required AgentLabelRepository LabelRepository { get; init; }
        public required NoopRedisService Redis { get; init; }
        public required Guid AgentId { get; init; }
        public required Guid SiteId { get; init; }
        public required Guid RuleId { get; init; }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public static Core.DTOs.AgentLabelRuleExpressionNodeDto HostnameContainsSrvExpression() => new()
        {
            NodeType = AgentLabelNodeType.Group,
            LogicalOperator = AgentLabelLogicalOperator.And,
            Children =
            [
                new Core.DTOs.AgentLabelRuleExpressionNodeDto
                {
                    NodeType = AgentLabelNodeType.Condition,
                    Field = AgentLabelField.Hostname,
                    Operator = AgentLabelComparisonOperator.StartsWith,
                    Value = "SRV"
                }
            ]
        };

        public static async Task<Fixture> CreateAsync(
            string hostname,
            AgentLabelApplyMode applyMode,
            Core.DTOs.AgentLabelRuleExpressionNodeDto? expression = null,
            IReadOnlyList<string>? installedSoftware = null,
            string ruleLabel = "PROD",
            AgentLabelLabelMatch labelMatch = AgentLabelLabelMatch.Exact)
        {
            var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
                .UseInMemoryDatabase($"auto-labeling-tests-{Guid.NewGuid():N}")
                .Options;

            var db = new LabelTestDbContext(options);

            var now = DateTime.UtcNow;
            var client = new Client { Id = Guid.NewGuid(), Name = "Client 01", CreatedAt = now, UpdatedAt = now };
            var site = new Site { Id = Guid.NewGuid(), ClientId = client.Id, Name = "Site 01", CreatedAt = now, UpdatedAt = now };
            var agent = new Agent
            {
                Id = Guid.NewGuid(),
                SiteId = site.Id,
                Hostname = hostname,
                Status = AgentStatus.Online,
                CreatedAt = now,
                UpdatedAt = now
            };

            var rule = new AgentLabelRule
            {
                Id = Guid.NewGuid(),
                Name = "Servidores",
                Label = ruleLabel,
                IsEnabled = true,
                ApplyMode = applyMode,
                LabelMatch = labelMatch,
                ExpressionJson = System.Text.Json.JsonSerializer.Serialize(
                    expression ?? HostnameContainsSrvExpression(),
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
                CreatedAt = now,
                UpdatedAt = now
            };

            db.Clients.Add(client);
            db.Sites.Add(site);
            db.Agents.Add(agent);
            db.AgentLabelRules.Add(rule);

            // Inventario de software opcional: necessario para exercitar os campos
            // SoftwareName/Publisher/Version (e a negacao em colecao).
            foreach (var softwareName in installedSoftware ?? [])
            {
                var catalog = new SoftwareCatalog
                {
                    Id = Guid.NewGuid(),
                    Name = softwareName,
                    Publisher = "Test",
                    Fingerprint = Guid.NewGuid().ToString("N"),
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.SoftwareCatalogs.Add(catalog);
                db.AgentSoftwareInventories.Add(new AgentSoftwareInventory
                {
                    Id = Guid.NewGuid(),
                    AgentId = agent.Id,
                    SoftwareId = catalog.Id,
                    IsPresent = true,
                    Version = "1.0.0",
                    CollectedAt = now,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            await db.SaveChangesAsync();

            var redis = new NoopRedisService();
            var service = new AgentAutoLabelingService(
                db,
                new AgentRepository(db),
                new AgentHardwareRepository(db),
                new AgentSoftwareRepository(db),
                new AgentLabelRuleRepository(db),
                new SiteRepository(db),
                redis,
                NullLogger<AgentAutoLabelingService>.Instance);

            return new Fixture
            {
                Db = db,
                Service = service,
                LabelRepository = new AgentLabelRepository(db),
                Redis = redis,
                AgentId = agent.Id,
                SiteId = site.Id,
                RuleId = rule.Id
            };
        }
    }

    /// <summary>
    /// Contexto restrito as entidades do auto-labeling. O DiscoveryDbContext completo
    /// mapeia dezenas de entidades cujas configuracoes dependem de recursos que o
    /// provider InMemory nao suporta, entao isolamos apenas o necessario.
    /// </summary>
    private sealed class LabelTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowedTypes = new HashSet<Type>
            {
                typeof(Client),
                typeof(Site),
                typeof(Agent),
                typeof(AgentLabelRule),
                typeof(AgentLabel),
                typeof(AgentLabelRuleMatch),
                typeof(AgentLabelChangeLog),
                typeof(AgentLabelSuppression),
                typeof(AgentLabelRuleVersion),
                typeof(AgentLabelProtectedLabel),
                typeof(SoftwareCatalog),
                typeof(AgentSoftwareInventory),
                typeof(AgentHardwareInfo),
                typeof(CustomFieldDefinition),
                typeof(CustomFieldValue)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowedTypes.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Site>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Agent>(entity => entity.HasKey(item => item.Id));

            modelBuilder.Entity<AgentLabelRule>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.ExpressionJson).IsRequired();
            });

            modelBuilder.Entity<AgentLabel>(entity => entity.HasKey(item => item.Id));

            modelBuilder.Entity<AgentLabelRuleMatch>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.HasIndex(item => new { item.RuleId, item.AgentId }).IsUnique();
            });

            modelBuilder.Entity<AgentLabelChangeLog>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AgentLabelRuleVersion>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AgentLabelProtectedLabel>(entity => entity.HasKey(item => item.Id));

            modelBuilder.Entity<AgentLabelSuppression>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.HasIndex(item => new { item.AgentId, item.Label }).IsUnique();
            });
            modelBuilder.Entity<CustomFieldDefinition>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<CustomFieldValue>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<SoftwareCatalog>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AgentSoftwareInventory>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AgentHardwareInfo>(entity => entity.HasKey(item => item.Id));
        }
    }

    private sealed class NoopRedisService : IRedisService
    {
        private readonly Dictionary<string, string> _store = [];

        public bool IsConnected => true;
        public Task<string?> GetAsync(string key) => Task.FromResult(_store.TryGetValue(key, out var v) ? v : null);
        public Task<long> IncrementAsync(string key) => Task.FromResult(0L);
        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(0L);
        public Task SetAsync(string key, string value, int expirySeconds = 3600)
        {
            _store[key] = value;
            return Task.CompletedTask;
        }
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(true);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(-1);
        public Task DeleteAsync(string key)
        {
            _store.Remove(key);
            return Task.CompletedTask;
        }
        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds) => Task.FromResult(true);
    }
}
