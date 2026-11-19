using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Permissões dedicadas do editor de Registro do Windows:
/// <c>Registry:View</c> (navegar/ler) e <c>Registry:Execute</c> (criar/editar/excluir).
///
/// <b>Por que separar de <c>Agents:Execute</c>:</b> consultar o registro é uma
/// operação de diagnóstico frequente e de baixo risco; alterá-lo pode quebrar o
/// sistema operacional de forma irreversível. Amarrar as duas ao mesmo
/// privilégio forçaria dar escrita a quem só precisa ler. O mesmo desenho existe
/// em RMMs maduros (ex.: a permissão <c>can_use_registry</c> do TacticalRMM),
/// e é o que permite um perfil "somente leitura de registro" no RBAC.
///
/// <b>Por que não reusar <c>RemoteSession:Execute</c>:</b> aquele recurso
/// controla a PREEMPÇÃO da sessão de outro técnico — um privilégio de risco
/// diferente, concedido a Admin/Manager/Operator. Misturar os dois faria a
/// concessão de um alterar o significado do outro.
///
/// <b>Concessão:</b> Admin, Manager e Operator recebem View e Execute (é o
/// conjunto que já opera acesso remoto hoje). Perfis de leitura (ex.: Auditoria)
/// podem receber apenas View via RBAC depois — é exatamente o caso de uso que
/// motivou a separação.
///
/// Idempotente: o <c>ON CONFLICT</c> e o <c>NOT EXISTS</c> permitem rodar em
/// banco que já tenha recebido a migração parcialmente.
/// </summary>
[Migration(20270121_288)]
public class M288_SeedRegistryPermission : Migration
{
    private static readonly (string Resource, string Action, string Description)[] Permissions =
    [
        ("Registry", "View", "Registry:View"),
        ("Registry", "Execute", "Registry:Execute"),
    ];

    private static readonly string[] RolesWithView = ["Admin", "Manager", "Operator"];
    private static readonly string[] RolesWithExecute = ["Admin", "Manager", "Operator"];

    public override void Up()
    {
        foreach (var (resource, action, description) in Permissions)
        {
            Execute.Sql($@"
                INSERT INTO permissions (id, resource_type, action_type, description)
                VALUES (gen_random_uuid(), '{resource}', '{action}', '{description}')
                ON CONFLICT (resource_type, action_type) DO NOTHING;
            ");
        }

        Grant("View", RolesWithView);
        Grant("Execute", RolesWithExecute);
    }

    private void Grant(string action, string[] roleNames)
    {
        // Interpolação de nomes de perfil é segura aqui: são literais de código
        // (não vêm de entrada), e o escapamento fica explícito de todo modo.
        var valuesList = string.Join(", ", roleNames.Select(r => $"'{r.Replace("'", "''")}'"));
        Execute.Sql($@"
            INSERT INTO role_permissions (role_id, permission_id)
            SELECT r.id, p.id
            FROM roles r
            JOIN permissions p
              ON p.resource_type = 'Registry'
             AND p.action_type = '{action}'
            WHERE r.name IN ({valuesList})
              AND r.is_system = true
              AND NOT EXISTS (
                  SELECT 1 FROM role_permissions rp
                  WHERE rp.role_id = r.id AND rp.permission_id = p.id
              );
        ");
    }

    public override void Down()
    {
        Execute.Sql(@"
            DELETE FROM role_permissions rp
            USING permissions p
            WHERE rp.permission_id = p.id
              AND p.resource_type = 'Registry';
        ");
        Execute.Sql(@"
            DELETE FROM permissions p
            WHERE p.resource_type = 'Registry'
              AND NOT EXISTS (SELECT 1 FROM role_permissions rp WHERE rp.permission_id = p.id);
        ");
    }
}
