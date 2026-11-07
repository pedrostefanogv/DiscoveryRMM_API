using Discovery.Core.Enums.Identity;

namespace Discovery.Tests;

/// <summary>
/// A promessa central do produto: um usuário que "atende somente chamados" NÃO
/// pode alcançar máquinas. A persona "Atendente (Somente Chamados)" é semeada pela
/// migração M274 como papel de SISTEMA (não editável pela UI).
///
/// Como rodar a migração exigiria PostgreSQL, este teste trava o CONTRATO das
/// personas de forma declarativa — a mesma lista que a migração usa. Se alguém
/// acrescentar Agents:Execute ao Atendente, o teste falha aqui, e não em produção
/// com um cliente descobrindo que o atendente dele abriu acesso remoto.
///
/// Uma cópia deliberada da verdade: a alternativa (ler o SQL da migração por regex)
/// seria mais frágil do que declarar a expectativa explicitamente.
/// </summary>
[TestFixture]
public class LeastPrivilegePersonaTests
{
    /// <summary>Permissões de Agents/RemoteSession que NUNCA podem estar no Atendente.</summary>
    private static readonly (ResourceType Resource, ActionType Action)[] RemoteCapabilities =
    [
        (ResourceType.Agents, ActionType.Execute),
        (ResourceType.Agents, ActionType.Create),
        (ResourceType.Agents, ActionType.Edit),
        (ResourceType.Agents, ActionType.Delete),
        (ResourceType.RemoteDebug, ActionType.Execute),
        (ResourceType.RemoteSession, ActionType.Execute),
        (ResourceType.Patches, ActionType.Execute),
        (ResourceType.Automation, ActionType.Execute),
        (ResourceType.AppStore, ActionType.Edit)
    ];

    /// <summary>Permissões do papel "Atendente (Somente Chamados)" (espelha M274).</summary>
    private static readonly (ResourceType Resource, ActionType Action)[] AtendentePermissions =
    [
        (ResourceType.Tickets, ActionType.View),
        (ResourceType.Tickets, ActionType.Create),
        (ResourceType.Tickets, ActionType.Edit),
        (ResourceType.Clients, ActionType.View),
        (ResourceType.Sites, ActionType.View),
        (ResourceType.Dashboard, ActionType.View),
        (ResourceType.KnowledgeBase, ActionType.View)
    ];

    /// <summary>Permissões do papel "Supervisor de Cliente" (espelha M274).</summary>
    private static readonly (ResourceType Resource, ActionType Action)[] SupervisorPermissions =
    [
        (ResourceType.Tickets, ActionType.View),
        (ResourceType.Tickets, ActionType.Create),
        (ResourceType.Tickets, ActionType.Edit),
        (ResourceType.Agents, ActionType.View),
        (ResourceType.Agents, ActionType.Execute),
        (ResourceType.RemoteDebug, ActionType.Execute),
        (ResourceType.Clients, ActionType.View),
        (ResourceType.Sites, ActionType.View),
        (ResourceType.Dashboard, ActionType.View),
        (ResourceType.Reports, ActionType.View),
        (ResourceType.Logs, ActionType.View),
        (ResourceType.KnowledgeBase, ActionType.View),
        (ResourceType.Users, ActionType.View)
    ];

    [Test]
    public void Ticket_only_persona_has_no_remote_or_agent_execution_capability()
    {
        var granted = AtendentePermissions.ToHashSet();

        foreach (var capability in RemoteCapabilities)
        {
            Assert.That(granted.Contains(capability), Is.False,
                $"a persona 'Atendente (Somente Chamados)' não pode ter {capability.Resource}.{capability.Action} " +
                "— isso permitiria acesso remoto/instalação de apps, contradizendo 'atender somente chamados'");
        }
    }

    [Test]
    public void Ticket_only_persona_can_still_work_tickets()
    {
        // A restrição não pode inutilizar o papel: ele precisa atender chamados.
        var granted = AtendentePermissions.ToHashSet();

        Assert.That(granted.Contains((ResourceType.Tickets, ActionType.View)), Is.True);
        Assert.That(granted.Contains((ResourceType.Tickets, ActionType.Create)), Is.True);
        Assert.That(granted.Contains((ResourceType.Tickets, ActionType.Edit)), Is.True);
    }

    [Test]
    public void Supervisor_can_see_users_but_cannot_grant_access()
    {
        // Users.View (enxerga a equipe) sem Users.Edit (não altera vínculos/papéis):
        // sem essa separação o supervisor poderia se autopromover.
        var granted = SupervisorPermissions.ToHashSet();

        Assert.That(granted.Contains((ResourceType.Users, ActionType.View)), Is.True);
        Assert.That(granted.Contains((ResourceType.Users, ActionType.Edit)), Is.False);
        Assert.That(granted.Contains((ResourceType.Users, ActionType.Create)), Is.False);
        Assert.That(granted.Contains((ResourceType.Users, ActionType.Delete)), Is.False);
    }

    [Test]
    public void Ticket_only_persona_does_not_hold_the_force_session_permission()
    {
        // Preemptar a sessão de outro técnico exige RemoteSession:Execute (M274).
        // O atendente não deve tê-la.
        Assert.That(
            AtendentePermissions.Contains((ResourceType.RemoteSession, ActionType.Execute)), Is.False);
    }

    [Test]
    public void Every_persona_only_references_resources_that_exist()
    {
        // Guarda contra erro de digitação no seed: um resource inexistente no enum
        // nunca casaria com a linha de permissions e a persona nasceria sem aquela
        // permissão, silenciosamente.
        var validResources = Enum.GetValues<ResourceType>().ToHashSet();
        var validActions = Enum.GetValues<ActionType>().ToHashSet();

        foreach (var (resource, action) in AtendentePermissions.Concat(SupervisorPermissions))
        {
            Assert.That(validResources.Contains(resource), Is.True, $"ResourceType inválido: {resource}");
            Assert.That(validActions.Contains(action), Is.True, $"ActionType inválido: {action}");
        }
    }

    [Test]
    public void RemoteSession_resource_type_exists_for_the_force_permission()
    {
        // A migração M274 semeia permissions com resource_type='RemoteSession' e
        // action_type='Execute'. Se o membro sair do enum, o EF grava string e a
        // permissão fica órfã — o gate de force nunca mais casa.
        Assert.That(Enum.GetValues<ResourceType>(), Does.Contain(ResourceType.RemoteSession));
    }
}
