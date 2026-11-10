using System.Reflection;
using Discovery.Api.Controllers;
using Discovery.Api.Filters;
using Discovery.Core.Enums.Identity;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Trava o contrato de permissão da tela de Labels Automáticas, que passou a viver
/// na Automação (/automation/agent-labels) mas continua operando sobre AGENTES.
///
/// <para>
/// Por que este teste existe: o front e o controller precisam concordar. Se o menu
/// usar automation.* e o controller exigir Agents.View, o usuário vê o link e leva
/// 403. O caminho inverso (empilhar os dois atributos) é pior: cada
/// <c>[RequirePermission]</c> vira um filtro independente e o ASP.NET exige TODOS
/// (AND), o que tira o acesso de papéis que só têm Agents.View.
/// </para>
/// </summary>
[TestFixture]
public class AgentLabelsPermissionTests
{
    private static IReadOnlyList<RequirePermissionAttribute> AttributesOf(Type type)
        => type.GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: false)
            .Cast<RequirePermissionAttribute>()
            .ToList();

    // O recurso/ação ficam em campos privados (o atributo não expõe getters).
    private static (ResourceType Resource, ActionType Action) ResourceOf(RequirePermissionAttribute attribute)
    {
        var resourceField = typeof(RequirePermissionAttribute)
            .GetField("_resource", BindingFlags.NonPublic | BindingFlags.Instance);
        var actionField = typeof(RequirePermissionAttribute)
            .GetField("_action", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.That(resourceField, Is.Not.Null);
        Assert.That(actionField, Is.Not.Null);

        return ((ResourceType)resourceField!.GetValue(attribute)!,
                (ActionType)actionField!.GetValue(attribute)!);
    }

    /// <summary>
    /// O controller NÃO pode exigir Automation.View em conjunto com Agents.View.
    ///
    /// <para>
    /// Empilhar os dois atributos cria um AND: papéis que só têm Agents.View
    /// (Supervisor de Cliente, Support, Técnico de Acesso Remoto) perderiam a tela,
    /// que eles enxergam hoje. Este teste falha se alguém "consertar" a divergência
    /// empilhando atributos.
    /// </para>
    /// </summary>
    [Test]
    public void Controller_nao_exige_Automation_em_conjunto_com_Agents()
    {
        var attributes = AttributesOf(typeof(AgentLabelsController));
        var granted = attributes.Select(ResourceOf).ToList();

        Assert.That(
            granted,
            Does.Contain((ResourceType.Agents, ActionType.View)),
            "a tela de labels exige Agents.View — é a permissão que governa os dados");

        Assert.That(
            granted,
            Does.Not.Contain((ResourceType.Automation, ActionType.View)),
            "exigir Agents.View E Automation.View (AND) tira o acesso de papéis que só " +
            "têm Agents.View; o menu deve espelhar Agents.View, não automation.*");
    }

    /// <summary>
    /// Guarda contra a regressão simétrica: TODAS as actions continuam protegidas.
    /// Antes de uma correção anterior, 10 de 15 endpoints estavam sem autorização.
    /// </summary>
    [Test]
    public void Toda_action_publica_tem_permissao_declarada()
    {
        var actions = typeof(AgentLabelsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName && method.GetParameters().Length > 0)
            .ToList();

        Assert.That(actions, Is.Not.Empty, "esperava actions HTTP no controller");

        foreach (var action in actions)
        {
            var hasAttribute = action
                .GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: false)
                .Length > 0;

            // A permissão pode vir do atributo de CLASSE (hoje é o caso) ou da action.
            var hasClassAttribute = AttributesOf(typeof(AgentLabelsController)).Count > 0;

            Assert.That(
                hasAttribute || hasClassAttribute,
                Is.True,
                $"a action {action.Name} ficou sem RequirePermission (nem na action, nem na classe)");
        }
    }
}
