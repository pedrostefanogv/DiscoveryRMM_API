using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// A2 - Runbook acessivel para quem atende (lacuna A3 da REVISAO_ROLES_PERMISSOES).
///
/// PROBLEMA: o endpoint POST /tickets/{id}/runbook/execute exige
/// Tickets:Execute (TicketsController.cs:297). Ate esta migracao, NENHUM papel
/// de sistema possuia essa permissao:
///   - M123 concede apenas Tickets View/Create/Edit/Delete;
///   - o Admin a recebe pelo CROSS JOIN da M069 (concessao global).
///
/// Consequencia pratica: o atendente abre o chamado, ve o runbook e leva 403
/// exatamente na acao mais natural do atendimento.
///
/// DECISAO (D2, ver docs_planejamento/DECISOES_RESOLVIDAS_PERMISSOES.md):
/// a permissao continua sendo condicao NECESSARIA, mas NAO suficiente. A regra
/// final e a conjuncao de tres condicoes, aplicada em
/// TicketAccessPolicy.CanExecuteOnMachineAsync:
///
///   pode executar runbook = usuario tem Tickets:Execute
///                      E  trata o chamado (time do departamento)
///                      E  department.can_execute_on_machines = true
///
/// Isto e o que mantem um departamento de COMPRAS (com chamado ligado a um
/// agent) incapaz de rodar script na maquina: a M279 deixou a coluna em FALSE
/// (fail-closed) e so habilita quem ja tinha runbook configurado.
///
/// Conceder Tickets:Execute aqui, portanto, NAO amplia o poder real de execucao
/// — apenas deixa de barrar antes de a regra de departamento ser avaliada.
///
/// Idempotente: pode ser reprocessada sem duplicar linhas.
/// </summary>
[Migration(20270114_282)]
public class M282_SeedRunbookExecutePermission : Migration
{
    public override void Up()
    {
        // Quem ATENDE precisa poder executar o runbook do chamado que trata.
        // O alcance continua limitado pelo vinculo com o departamento e pela
        // habilitacao explicita do departamento (M279).
        foreach (var persona in AttendeePersonas)
        {
            Grant(persona, "Tickets", new[] { "Execute" });
        }

        // O gestor do departamento administra o time; nao ha razao para ele
        // enxergar o runbook e nao poder executa-lo no proprio departamento.
        Grant("Gestor do Departamento", "Tickets", new[] { "Execute" });

        // Papeis base de atendimento (M123). Operator e o tecnico generico;
        // Manager supervisiona a operacao. Support fica DE FORA de proposito:
        // ver D6(a) - ele exclui chamado mas a decisao foi nao ampliar execucao
        // de automacao para o perfil de suporte.
        Grant("Operator", "Tickets", new[] { "Execute" });
        Grant("Manager", "Tickets", new[] { "Execute" });
    }

    /// <summary>
    /// Personas de atendimento (criadas na M274). Mesma lista da M278.
    /// </summary>
    private static readonly string[] AttendeePersonas =
    {
        "Atendente (Somente Chamados)",
        "Tecnico de Acesso Remoto",
        "Tecnico de Campo",
        "Supervisor de Cliente"
    };

    /// <summary>
    /// Concede (resource, action) a um papel de sistema. Idempotente: o
    /// NOT EXISTS evita duplicar quando a migracao e reprocessada ou quando o
    /// papel ja tinha a permissao por outra migracao.
    /// </summary>
    private void Grant(string roleName, string resource, string[] actions)
    {
        var escapedRole = roleName.Replace("'", "''");
        var escapedResource = resource.Replace("'", "''");
        var actionList = string.Join(", ", actions.Select(x => "'" + x + "'"));

        Execute.Sql($@"
            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p
              ON p.resource_type = '{escapedResource}'
             AND p.action_type IN ({actionList})
            WHERE r.name = '{escapedRole}'
              AND r.is_system = true
              AND NOT EXISTS (
                  SELECT 1 FROM role_permissions rp
                  WHERE rp.role_id = r.id AND rp.permission_id = p.id
              );
        ");
    }

    public override void Down()
    {
        // Remove apenas o que ESTA migracao concedeu.
        GrantDown("Operator", "Tickets");
        GrantDown("Manager", "Tickets");

        foreach (var persona in AttendeePersonas)
        {
            GrantDown(persona, "Tickets");
        }

        GrantDown("Gestor do Departamento", "Tickets");
    }

    private void GrantDown(string roleName, string resource)
    {
        var escapedRole = roleName.Replace("'", "''");
        var escapedResource = resource.Replace("'", "''");

        // NAO remove Tickets:View/Create/Edit que outras migracoes concederam:
        // apenas a acao Execute desta migracao.
        Execute.Sql($@"
            DELETE FROM role_permissions rp
            USING roles r, permissions p
            WHERE rp.role_id = r.id
              AND rp.permission_id = p.id
              AND r.name = '{escapedRole}'
              AND r.is_system = true
              AND p.resource_type = '{escapedResource}'
              AND p.action_type = 'Execute';
        ");
    }
}
