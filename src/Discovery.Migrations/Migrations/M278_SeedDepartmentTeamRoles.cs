using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Ajustes de RBAC decorrentes da autorizacao por DEPARTAMENTO/TIME DE ATENDIMENTO.
///
/// Contexto: o vinculo em department_members deixou de ser apenas roteamento
/// (round-robin/triagem por IA) e passou a AUTORIZAR quem pode tratar um chamado.
/// Isso criou tres lacunas nos papeis padrao:
///
/// 1. Gestor do departamento (D1) - nenhuma das personas podia editar o time.
///    Com o vinculo autorizando, um departamento sem gestor ficaria com o time
///    congelado (so Admin/Manager global conseguiriam mante-lo).
///
/// 2. Ver a equipe (D3) - GET /departments/{id}/members exige Departments:View,
///    que as personas nao tinham. O atendente precisa ver o time para
///    transferir/delegar um chamado a um colega.
///
/// 3. Publicar na base de conhecimento (D4) - KnowledgeBase:Edit era exclusivo
///    do Admin. Quem atende o departamento deve poder documentar a solucao.
///    O alcance continua limitado pelo escopo de cliente/site do grupo.
///
/// Idempotente: pode ser reprocessada sem duplicar linhas.
/// </summary>
[Migration(20270112_278)]
public class M278_SeedDepartmentTeamRoles : Migration
{
    public override void Up()
    {
        // -- 1. D1: papel de gestao do departamento ----------------------
        //
        // Papel NOVO (nao reusa Supervisor de Cliente) para nao ampliar o
        // privilegio de um papel que ja existe e e atribuido por outros motivos.
        Execute.Sql(@"
            INSERT INTO roles (id, name, description, type, is_system, created_at, updated_at)
            SELECT gen_random_uuid(),
                   'Gestor do Departamento',
                   'Gerencia o time de atendimento do departamento: inclui/remove atendentes e define competencias. Nao administra a configuracao do servidor.',
                   'System', true, now(), now()
            WHERE NOT EXISTS (
                SELECT 1 FROM roles r WHERE r.name = 'Gestor do Departamento' AND r.is_system = true
            );
        ");

        // O gestor VE e EDITA departamentos (equipe, competencias, config de
        // triagem), mas NAO exclui o departamento (acao destrutiva que segue
        // com Admin/Manager).
        Grant("Gestor do Departamento", "Departments", new[] { "View", "Create", "Edit" });

        // Para exercer o papel com contexto, o gestor precisa enxergar o que
        // cerca o time.
        Grant("Gestor do Departamento", "Tickets", new[] { "View", "Create", "Edit" });
        Grant("Gestor do Departamento", "Users", new[] { "View" });
        Grant("Gestor do Departamento", "Clients", new[] { "View" });
        Grant("Gestor do Departamento", "Sites", new[] { "View" });
        Grant("Gestor do Departamento", "Workflow", new[] { "View" });
        Grant("Gestor do Departamento", "Sla", new[] { "View" });
        Grant("Gestor do Departamento", "Dashboard", new[] { "View" });
        Grant("Gestor do Departamento", "Reports", new[] { "View" });

        // -- 2. D3: personas passam a ver o departamento e a equipe --------
        foreach (var persona in AttendeePersonas)
        {
            Grant(persona, "Departments", new[] { "View" });
            Grant(persona, "Workflow", new[] { "View" });
            Grant(persona, "Sla", new[] { "View" });
        }

        // -- 3. D4: quem atende pode documentar na base de conhecimento ----
        //
        // Concede Create+Edit (nao Delete): publicar e corrigir artigo, sem
        // apagar o trabalho de colegas. O alcance continua limitado pelo
        // escopo de cliente/site do grupo do usuario.
        foreach (var persona in AttendeePersonas)
        {
            Grant(persona, "KnowledgeBase", new[] { "Create", "Edit" });
        }

        // Papeis base de atendimento tambem documentam (mesma logica do D4).
        Grant("Operator", "KnowledgeBase", new[] { "Create", "Edit" });
        Grant("Manager", "KnowledgeBase", new[] { "Create", "Edit" });
    }

    /// <summary>
    /// Personas de atendimento (criadas na M274). O gestor do departamento e
    /// tratado a parte por ter privilegios proprios.
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
        GrantDown("Operator", "KnowledgeBase");
        GrantDown("Manager", "KnowledgeBase");

        foreach (var persona in AttendeePersonas)
        {
            GrantDown(persona, "Departments");
            GrantDown(persona, "Workflow");
            GrantDown(persona, "Sla");
            GrantDown(persona, "KnowledgeBase");
        }

        // O papel de gestao e removido por inteiro.
        Execute.Sql(@"
            DELETE FROM role_permissions rp
            USING roles r
            WHERE rp.role_id = r.id
              AND r.name = 'Gestor do Departamento'
              AND r.is_system = true;
        ");
        Execute.Sql(@"
            DELETE FROM roles
            WHERE name = 'Gestor do Departamento' AND is_system = true;
        ");
    }

    private void GrantDown(string roleName, string resource)
    {
        var escapedRole = roleName.Replace("'", "''");
        var escapedResource = resource.Replace("'", "''");

        Execute.Sql($@"
            DELETE FROM role_permissions rp
            USING roles r, permissions p
            WHERE rp.role_id = r.id
              AND rp.permission_id = p.id
              AND r.name = '{escapedRole}'
              AND r.is_system = true
              AND p.resource_type = '{escapedResource}';
        ");
    }
}
