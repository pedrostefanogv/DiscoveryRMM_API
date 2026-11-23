using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Remove as policies ÓRFÃS das tools que foram unificadas em famílias
/// action-based (software, event_log, local_memory, screen) ou removidas
/// (navegação interna).
///
/// Contexto: o auto-registro do agente (McpToolPolicyRepository.EnsureGlobalPoliciesAsync)
/// só INSERE a policy global de cada tool registrada e nunca apaga a linha de uma
/// tool que deixou de existir. Ao consolidar ~19 tools em 4 famílias, as linhas
/// antigas continuaram em mcp_tool_policies das instalações já existentes e a tela
/// /settings/mcp-tools passou a listar 76 tools (13 de servidor + 62 "de agente"),
/// enquanto uma instalação nova (que nunca registrou os nomes antigos) listava 57.
///
/// O McpToolGovernance.GetCatalogAsync também filtra nomes substituídos em runtime
/// (McpToolCatalogMetadata.SupersededTools), para que um agente desatualizado que
/// volte a registrar um nome legado não repovoe a tela; esta migration cuida da
/// limpeza física das linhas que já existem.
///
/// Preservação de intenção: um override de escopo RESTRITIVO (is_enabled=false ou
/// locked=true) sobre uma tool antiga é PORTADO para a família substituta quando
/// ainda não existe linha da família naquele mesmo escopo — fail-closed, uma
/// restrição deliberada nunca vira permissão silenciosa. Overrides apenas
/// habilitados não são portados: a família já nasce habilitada (M293), então
/// copiá-los só herdaria um timeout/limite específico da ação antiga, que não faz
/// sentido para a família inteira.
///
/// Uma restrição GLOBAL sobre uma ação antiga também NÃO é portada: a família
/// cobre várias ações (ex.: software = search + install + upgrade + uninstall) e
/// desabilitá-la inteira seria MAIS amplo do que o operador pediu. Como a tool
/// antiga não existe mais em agente nenhum, essa restrição global já era inócua —
/// a linha é apenas removida, e quem quiser restringir a família a configura em
/// /settings/mcp-tools.
///
/// Idempotente: NOT EXISTS/PORT + DELETE por nome; reexecutar não altera nada.
/// </summary>
[Migration(20270128_295)]
public class M295_RemoveSupersededMcpToolPolicies : Migration
{
    /// <summary>
    /// Nomes legados removidos. Espelha McpToolCatalogMetadata.SupersededTools
    /// (a migration é SQL puro e não referencia tipos de Infrastructure).
    /// </summary>
    private static readonly string[] SoftwareLegacy =
    [
        "search_packages", "list_installed_packages", "get_pending_updates", "get_package_actions",
        "install_package", "uninstall_package", "upgrade_package", "upgrade_all_packages"
    ];

    private static readonly string[] EventLogLegacy =
    [
        "query_event_log", "get_recent_errors", "get_recent_crashes"
    ];

    private static readonly string[] LocalMemoryLegacy =
    [
        "memory_list", "memory_create", "memory_delete"
    ];

    private static readonly string[] ScreenLegacy =
    [
        "capture_screenshot", "list_open_windows", "screenshot_permission"
    ];

    private static readonly string[] RemovedWithoutReplacement =
    [
        "get_internal_navigation_routes", "build_internal_navigation_link"
    ];

    /// <summary>
    /// Nomes removidos por esta migration. Usado pelo teste de coerência que
    /// trava esta lista contra <c>McpToolCatalogMetadata.SupersededTools</c>:
    /// o filtro de runtime e a limpeza física não podem divergir.
    /// </summary>
    public static readonly string[] SupersededToolNames = AllLegacyNames();

    public override void Up()
    {
        // 1. Porta overrides restritivos para a família substituta (fail-closed).
        PortRestrictiveOverrides("software", SoftwareLegacy, defaultMaxCalls: 10, defaultTimeoutSeconds: 900);
        PortRestrictiveOverrides("event_log", EventLogLegacy, defaultMaxCalls: 20, defaultTimeoutSeconds: 45);
        PortRestrictiveOverrides("local_memory", LocalMemoryLegacy, defaultMaxCalls: 20, defaultTimeoutSeconds: 15);
        PortRestrictiveOverrides("screen", ScreenLegacy, defaultMaxCalls: 10, defaultTimeoutSeconds: 0);

        // 2. Remove as linhas legadas (globais E de escopo). Os nomes de navegação
        //    não têm substituta: as ações hoje são resolvidas por cards/A2UI.
        Execute.Sql($"""
            DELETE FROM mcp_tool_policies
             WHERE tool_name IN ({Literal(AllLegacyNames())});
            """);
    }

    /// <summary>
    /// Não há Down: as linhas removidas eram órfãs (nenhum handler/schema vivo) e
    /// recriá-las só devolveria o defeito que esta migration corrige. Os overrides
    /// restritivos portados continuam valendo na família e não podem ser
    /// distinguidos dos configurados manualmente.
    /// </summary>
    public override void Down() { }

    private void PortRestrictiveOverrides(
        string replacement, IReadOnlyCollection<string> legacyNames,
        int defaultMaxCalls, int defaultTimeoutSeconds)
    {
        Execute.Sql($"""
            INSERT INTO mcp_tool_policies (
                id, client_id, site_id, agent_id, tool_name, source, description,
                is_enabled, max_calls_per_minute, timeout_seconds, locked,
                argument_schema_json, created_at, updated_at
            )
            SELECT
                gen_random_uuid(),
                s.client_id, s.site_id, s.agent_id,
                '{replacement}', 'agent', NULL,
                s.is_enabled,
                COALESCE(
                    rep.max_calls_per_minute,
                    (SELECT g.max_calls_per_minute FROM mcp_tool_policies g
                      WHERE g.tool_name = '{replacement}'
                        AND g.client_id IS NULL AND g.site_id IS NULL AND g.agent_id IS NULL),
                    {defaultMaxCalls}),
                COALESCE(
                    rep.timeout_seconds,
                    (SELECT g.timeout_seconds FROM mcp_tool_policies g
                      WHERE g.tool_name = '{replacement}'
                        AND g.client_id IS NULL AND g.site_id IS NULL AND g.agent_id IS NULL),
                    {defaultTimeoutSeconds}),
                s.locked,
                NULL, NOW(), NOW()
            FROM (
                SELECT DISTINCT ON (l.client_id, l.site_id, l.agent_id)
                       l.client_id, l.site_id, l.agent_id, l.is_enabled, l.locked
                  FROM mcp_tool_policies l
                 WHERE l.tool_name IN ({Literal(legacyNames)})
                   AND (l.is_enabled = FALSE OR l.locked = TRUE)
                   AND (l.client_id IS NOT NULL OR l.site_id IS NOT NULL OR l.agent_id IS NOT NULL)
                 ORDER BY l.client_id, l.site_id, l.agent_id, l.is_enabled ASC, l.locked DESC
            ) s
            LEFT JOIN mcp_tool_policies rep
                   ON rep.tool_name = '{replacement}'
                  AND rep.client_id IS NOT DISTINCT FROM s.client_id
                  AND rep.site_id IS NOT DISTINCT FROM s.site_id
                  AND rep.agent_id IS NOT DISTINCT FROM s.agent_id
            WHERE rep.id IS NULL;
            """);
    }

    private static string[] AllLegacyNames() =>
    [
        .. SoftwareLegacy, .. EventLogLegacy, .. LocalMemoryLegacy, .. ScreenLegacy,
        .. RemovedWithoutReplacement
    ];

    /// <summary>Lista de literais SQL escapados: 'a', 'b', ...</summary>
    private static string Literal(IReadOnlyCollection<string> names) =>
        string.Join(", ", names.Select(n => "'" + n.Replace("'", "''") + "'"));
}
