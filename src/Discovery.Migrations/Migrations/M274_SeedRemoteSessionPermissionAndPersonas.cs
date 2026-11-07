using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Semeia as permissões de sessão remota e as personas de menor privilégio.
///
/// Dois problemas resolvidos aqui:
///
/// 1. <b>RemoteSession:Execute</b> — antes, encerrar/substituir a sessão remota de
///    OUTRO técnico (<c>force=true</c>) exigia apenas <c>Agents:Execute</c>, a mesma
///    permissão usada para abrir a própria sessão. Qualquer operador podia derrubar
///    o atendimento em andamento de um colega (e o console decidia isso por regex de
///    texto de erro). Passa a existir permissão dedicada.
///
/// 2. <b>Personas de menor privilégio</b> — o produto promete um usuário que
///    "atende somente chamados". Antes disso, o caminho era conceder um papel amplo
///    e torcer para a UI esconder o resto. As personas abaixo são papéis de SISTEMA
///    (não editáveis pela UI) e a mais restrita delas não possui NENHUMA permissão
///    de agente/acesso remoto — a restrição vale no servidor, não no menu.
///
/// Papéis existentes recebem apenas o que lhes cabe:
/// Admin/Manager = Force sim; Operator = Force sim (já opera sessões);
/// Support/Viewer = Force NÃO.
/// </summary>
[Migration(20270110_274)]
public class M274_SeedRemoteSessionPermissionAndPersonas : Migration
{
    public override void Up()
    {
        // ── 1. Permissão dedicada de preempção de sessão remota ──────────────
        Execute.Sql(@"
            INSERT INTO permissions (id, resource_type, action_type, description)
            VALUES (gen_random_uuid(), 'RemoteSession', 'Execute', 'RemoteSession:Execute')
            ON CONFLICT (resource_type, action_type) DO NOTHING;
        ");

        GrantForceToRole("Admin");
        GrantForceToRole("Manager");
        GrantForceToRole("Operator");

        // ── 2. Personas de sistema (menor privilégio) ────────────────────────
        SeedPersona(
            "Atendente (Somente Chamados)",
            "Atende chamados no escopo do cliente/site. NÃO acessa máquinas, NÃO instala aplicativos e NÃO abre sessão remota.",
            new[]
            {
                ("Tickets", "View"), ("Tickets", "Create"), ("Tickets", "Edit"),
                ("Clients", "View"),
                ("Sites", "View"),
                ("Dashboard", "View"),
                ("KnowledgeBase", "View")
            });

        SeedPersona(
            "Tecnico de Acesso Remoto",
            "Atende chamados e acessa remotamente as maquinas do escopo. Nao administra configuracao nem usuarios.",
            new[]
            {
                ("Tickets", "View"), ("Tickets", "Create"), ("Tickets", "Edit"),
                ("Agents", "View"), ("Agents", "Execute"),
                ("RemoteDebug", "Execute"),
                ("Clients", "View"),
                ("Sites", "View"),
                ("Dashboard", "View"),
                ("KnowledgeBase", "View")
            });

        SeedPersona(
            "Tecnico de Campo",
            "Acesso remoto, instalacao de aplicativos e patches no escopo. Nao administra usuarios.",
            new[]
            {
                ("Tickets", "View"), ("Tickets", "Create"), ("Tickets", "Edit"),
                ("Agents", "View"), ("Agents", "Create"), ("Agents", "Edit"), ("Agents", "Execute"),
                ("RemoteDebug", "Execute"),
                ("AppStore", "View"), ("AppStore", "Edit"),
                ("Patches", "View"), ("Patches", "Edit"), ("Patches", "Execute"),
                ("Deployment", "View"),
                ("Automation", "View"), ("Automation", "Execute"),
                ("Clients", "View"),
                ("Sites", "View"),
                ("Dashboard", "View"),
                ("Reports", "View"),
                ("Logs", "View"),
                ("KnowledgeBase", "View")
            });

        SeedPersona(
            "Supervisor de Cliente",
            "Supervisiona o escopo do cliente: ve relatorios e usuarios, mas NAO concede acesso (sem Users:Edit) nem forca sessao remota.",
            new[]
            {
                ("Tickets", "View"), ("Tickets", "Create"), ("Tickets", "Edit"),
                ("Agents", "View"), ("Agents", "Execute"),
                ("RemoteDebug", "Execute"),
                ("Clients", "View"),
                ("Sites", "View"),
                ("Dashboard", "View"),
                ("Reports", "View"),
                ("Logs", "View"),
                ("KnowledgeBase", "View"),
                // Users:View (sem Edit) — enxerga a equipe, nao altera vinculos.
                ("Users", "View")
            });
    }

    /// <summary>
    /// Cria (ou completa) um papel de sistema e concede exatamente as permissões
    /// listadas. Idempotente: reprocessar a migração não duplica nada.
    /// </summary>
    private void SeedPersona(string roleName, string description, (string Resource, string Action)[] permissions)
    {
        var escapedName = roleName.Replace("'", "''");
        var escapedDescription = description.Replace("'", "''");

        // type/is_system/mfa_requirement/is_active têm default no schema; declaramos
        // apenas o que é semântico (System + is_system) para não acoplar esta migração
        // a colunas que evoluíram em migrações posteriores (M076/M195).
        Execute.Sql($@"
            INSERT INTO roles (id, name, description, type, is_system, created_at, updated_at)
            SELECT gen_random_uuid(), '{escapedName}', '{escapedDescription}', 'System', true, now(), now()
            WHERE NOT EXISTS (SELECT 1 FROM roles r WHERE r.name = '{escapedName}' AND r.is_system = true);
        ");

        var valuesList = string.Join(", ", permissions.Select(p => $"('{p.Resource}', '{p.Action}')"));
        Execute.Sql($@"
            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p ON (p.resource_type, p.action_type) IN ({valuesList})
            WHERE r.name = '{escapedName}'
              AND r.is_system = true
              AND NOT EXISTS (
                  SELECT 1 FROM role_permissions rp
                  WHERE rp.role_id = r.id AND rp.permission_id = p.id
              );
        ");
    }

    private void GrantForceToRole(string roleName)
    {
        Execute.Sql($@"
            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p
              ON p.resource_type = 'RemoteSession'
             AND p.action_type = 'Execute'
            WHERE r.name = '{roleName}'
              AND r.is_system = true
              AND NOT EXISTS (
                  SELECT 1 FROM role_permissions rp
                  WHERE rp.role_id = r.id AND rp.permission_id = p.id
              );
        ");
    }

    public override void Down()
    {
        foreach (var role in new[]
                 {
                     "Atendente (Somente Chamados)", "Tecnico de Acesso Remoto",
                     "Tecnico de Campo", "Supervisor de Cliente"
                 })
        {
            var escaped = role.Replace("'", "''");
            Execute.Sql($@"
                DELETE FROM role_permissions rp
                USING roles r
                WHERE rp.role_id = r.id AND r.name = '{escaped}' AND r.is_system = true;
            ");
            Execute.Sql($@"
                DELETE FROM roles WHERE name = '{escaped}' AND is_system = true;
            ");
        }

        Execute.Sql(@"
            DELETE FROM role_permissions rp
            USING permissions p
            WHERE rp.permission_id = p.id
              AND p.resource_type = 'RemoteSession'
              AND p.action_type = 'Execute';
        ");
        Execute.Sql(@"
            DELETE FROM permissions p
            WHERE p.resource_type = 'RemoteSession' AND p.action_type = 'Execute'
              AND NOT EXISTS (SELECT 1 FROM role_permissions rp WHERE rp.permission_id = p.id);
        ");
    }
}
