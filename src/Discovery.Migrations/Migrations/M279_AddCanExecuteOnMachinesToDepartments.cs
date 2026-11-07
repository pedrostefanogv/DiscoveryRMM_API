using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Execucao em maquina por departamento (D2).
///
/// Nem todo departamento deve executar automacao nas maquinas. Um chamado de
/// COMPRAS ("comprar um monitor") pode estar vinculado a um agent e nem por isso
/// o time de compras deve rodar script no equipamento.
///
/// Antes, o runbook exigia apenas Tickets:Execute. Como o vinculo com o chamado
/// agora AUTORIZA tratar, sem esta coluna o poder de execucao viria junto com o
/// vinculo — e todo departamento com chamado ligado a uma maquina poderia rodar
/// script nela.
///
/// A regra final e a CONJUNCAO de duas condicoes:
///   permissao Tickets:Execute do usuario  E  department.can_execute_on_machines.
///
/// Padrao FALSE (fail-closed). ATENCAO no rollout: departamentos que hoje usam
/// runbook precisam ser habilitados, senao param de executar. Ver
/// docs_planejamento/ROLLOUT_*.md.
/// </summary>
[Migration(20270112_279)]
public class M279_AddCanExecuteOnMachinesToDepartments : Migration
{
    public override void Up()
    {
        // DEFAULT false atende os departamentos existentes e os novos.
        Alter.Table("departments")
            .AddColumn("can_execute_on_machines")
            .AsBoolean()
            .NotNullable()
            .WithDefaultValue(false);

        // Habilita retroativamente os departamentos que JA tem runbook em uso,
        // identificados por possuirem macro ATIVA com passos configurados. Sem
        // isso, quem usa a funcionalidade hoje pararia de executar apos o deploy.
        //
        // O criterio e conservador de proposito: so entram departamentos com
        // macro de runbook REALMENTE configurada — nao todos os que tem chamado
        // com agent vinculado.
        Execute.Sql(@"
            UPDATE departments d
            SET can_execute_on_machines = true
            WHERE EXISTS (
                SELECT 1 FROM ticket_macros m
                WHERE m.department_id = d.id
                  AND m.is_active = true
                  AND m.runbook_steps_json IS NOT NULL
                  AND m.runbook_steps_json <> 'null'::jsonb
                  AND m.runbook_steps_json <> '[]'::jsonb
            );
        ");
    }

    public override void Down()
    {
        Delete.Column("can_execute_on_machines").FromTable("departments");
    }
}
