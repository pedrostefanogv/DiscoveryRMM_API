using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Services;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Policy-sync: tarefa RunScript cujo script foi desativado/excluído DEPOIS da
/// criação não pode ser entregue ao agent. Antes ela ia na policy com Script=null
/// e o agent falhava a execução com exit 2 ("script nao resolvido para a tarefa")
/// a cada gatilho. Criar/editar tarefa RunScript já exige script ativo.
/// </summary>
public class AutomationPolicyDeliverableTasksTests
{
    private static AutomationTaskDefinition ScriptTask(Guid? scriptId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Rodar script",
        ActionType = AutomationTaskActionType.RunScript,
        ScriptId = scriptId,
        IsActive = true
    };

    private static AutomationScriptDefinition Script(Guid id) => new()
    {
        Id = id,
        Name = "Coletar ID",
        Content = "Write-Output 1",
        IsActive = true
    };

    [Test]
    public void MantemRunScriptQuandoOScriptEstaDisponivel()
    {
        var scriptId = Guid.NewGuid();
        var scripts = new Dictionary<Guid, AutomationScriptDefinition> { [scriptId] = Script(scriptId) };

        var result = AutomationTaskService.FilterDeliverableTasks([ScriptTask(scriptId)], scripts);

        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public void RemoveRunScriptQuandoOScriptEstaInativoOuExcluido()
    {
        // Script inativo não é carregado pelo repositório (includeInactive: false),
        // então o mapa fica sem a chave.
        var scripts = new Dictionary<Guid, AutomationScriptDefinition>();

        var result = AutomationTaskService.FilterDeliverableTasks([ScriptTask(Guid.NewGuid())], scripts);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void RemoveRunScriptSemScriptId()
    {
        var result = AutomationTaskService.FilterDeliverableTasks(
            [ScriptTask(null)],
            new Dictionary<Guid, AutomationScriptDefinition>());

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void MantemTarefasQueNaoDependemDeScript()
    {
        var scripts = new Dictionary<Guid, AutomationScriptDefinition>();
        var tasks = new List<AutomationTaskDefinition>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Instalar",
                ActionType = AutomationTaskActionType.InstallPackage,
                PackageId = "Google.Chrome",
                IsActive = true
            },
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Comando",
                ActionType = AutomationTaskActionType.CustomCommand,
                CommandPayload = "whoami",
                IsActive = true
            }
        };

        var result = AutomationTaskService.FilterDeliverableTasks(tasks, scripts);

        Assert.That(result, Has.Count.EqualTo(2));
    }
}
