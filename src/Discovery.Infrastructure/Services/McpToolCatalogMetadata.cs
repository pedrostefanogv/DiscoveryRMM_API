using Discovery.Core.Entities;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Metadados editoriais do catálogo de MCP tools exibido na tela de governança
/// (/settings/mcp-tools): categoria, "quando usar" e timeout RECOMENDADO.
///
/// Por que existe: a tela listava dezenas de ferramentas com uma descrição
/// genérica ("Ferramenta do agente...") e um timeout padrão único (10s no
/// servidor, 60s no agente) — ferramentas como install_package/upgrade_all_packages
/// levam minutos, enquanto time.current é instantânea. As recomendações aqui são
/// SUGESTÕES: o operador continua livre para sobrescrever por escopo.
/// </summary>
public static class McpToolCatalogMetadata
{
    /// <param name="Category">Agrupamento exibido na tela (ex.: Software, Rede).</param>
    /// <param name="WhenToUse">Orientação curta de "quando usar".</param>
    /// <param name="RecommendedTimeoutSeconds">Timeout sugerido; 0 = não se aplica.</param>
    /// <param name="TimeoutApplies">
    /// false para tools que aguardam interação do usuário (ask_user,
    /// capture_screenshot, read_file): o agente NÃO aplica o timeout nelas, então
    /// configurar um valor só gera expectativa falsa.
    /// </param>
    public sealed record ToolMetadata(
        string Category,
        string? WhenToUse,
        int RecommendedTimeoutSeconds,
        bool TimeoutApplies);

    private static readonly IReadOnlyDictionary<string, ToolMetadata> Known =
        new Dictionary<string, ToolMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Tools de SERVIDOR (executadas na API) ────────────────────────
            ["knowledge_search"] = new("Base de conhecimento",
                "Use quando o usuário perguntar sobre políticas, procedimentos, sistemas internos ou assuntos documentados da empresa.",
                30, true),
            ["knowledge_list"] = new("Base de conhecimento",
                "Use para listar/confirmar quais artigos existem antes de dizer que não há conteúdo sobre um assunto.",
                30, true),
            ["time.current"] = new("Utilitários",
                "Use para SLA, prazos e contexto temporal. É instantânea — o timeout só protege contra travas.",
                10, true),
            ["sequential_thinking"] = new("Utilitários",
                "Use apenas em diagnósticos que exigem raciocínio em várias etapas (CPU + memória + disco + rede).",
                15, true),
            ["memory.search"] = new("Memória (servidor)",
                "Use no início de uma conversa NOVA para recordar problemas e soluções de conversas anteriores desta máquina. Cobre o histórico persistente do servidor (não a conversa atual).",
                20, true),

            // ── Software (winget) ────────────────────────────────────────────
            ["search_packages"] = new("Software",
                "Use para buscar programas no catálogo winget antes de instalar/atualizar.",
                60, true),
            ["install_package"] = new("Software",
                "Use para instalar um programa já localizado por search_packages. Instalação winget pode levar minutos.",
                900, true),
            ["uninstall_package"] = new("Software",
                "Use para desinstalar um programa (destrutiva: exige confirm=true).",
                600, true),
            ["upgrade_package"] = new("Software",
                "Use para atualizar um programa específico. Pode levar minutos.",
                900, true),
            ["upgrade_all_packages"] = new("Software",
                "Use para atualizar TODOS os programas com atualização disponível (operação longa, em lote).",
                1800, true),
            ["list_installed_packages"] = new("Software",
                "Use para listar os programas instalados detectados pelo winget.",
                120, true),
            ["get_pending_updates"] = new("Software",
                "Use para listar atualizações disponíveis com versão atual e disponível.",
                120, true),
            ["get_package_actions"] = new("Software",
                "Use para saber a ação contextual (install/uninstall/upgrade) de cada pacote.",
                30, true),

            // ── Inventário ───────────────────────────────────────────────────
            ["get_inventory"] = new("Inventário",
                "Use para hardware, SO, discos, rede, usuários, bateria, BitLocker e software instalado.",
                90, true),
            ["export_inventory_markdown"] = new("Inventário",
                "Use para gerar o relatório de inventário em Markdown.",
                120, true),
            ["export_inventory_pdf"] = new("Inventário",
                "Use para gerar o relatório de inventário em PDF.",
                180, true),
            ["get_agent_info"] = new("Inventário",
                "Use ANTES de abrir chamado para enriquecer a descrição com dados da máquina.",
                30, true),
            ["get_logs"] = new("Diagnóstico",
                "Use para ver os logs recentes das operações de software (instalação/atualização).",
                30, true),

            // ── Diagnóstico ──────────────────────────────────────────────────
            ["get_performance_snapshot"] = new("Diagnóstico",
                "Use para um retrato rápido de CPU, memória e uso de disco por volume.",
                60, true),
            ["query_event_log"] = new("Diagnóstico",
                "Use para consultar o Windows Event Log com filtros de log, nível, fonte e janela de tempo.",
                120, true),
            ["get_recent_errors"] = new("Diagnóstico",
                "Use para erros/críticos recentes dos logs System e Application.",
                120, true),
            ["get_recent_crashes"] = new("Diagnóstico",
                "Use para BSODs, falhas de kernel, desligamentos inesperados e crashes de aplicativo.",
                120, true),
            ["osquery"] = new("Diagnóstico",
                "Use para consultas osquery read-only (inventário/diagnóstico avançado).",
                120, true),
            ["disk"] = new("Diagnóstico",
                "Use para saúde, uso e temperatura dos discos.",
                60, true),
            ["process_control"] = new("Diagnóstico",
                "Use para listar/ordenar processos e encerrar um processo (kill exige confirm=true).",
                60, true),

            // ── Rede ─────────────────────────────────────────────────────────
            ["network_diagnostics"] = new("Rede",
                "Use para ping, DNS, conexões, resolução de nomes e configuração de rede local.",
                120, true),

            // ── Sistema ──────────────────────────────────────────────────────
            ["system_info"] = new("Sistema",
                "Use para informações do sistema operacional e da máquina.",
                60, true),
            ["service_control"] = new("Sistema",
                "Use para consultar/iniciar/parar serviços do Windows (ações exigem confirm=true).",
                120, true),
            ["windows_update"] = new("Sistema",
                "Use para consultar/instalar atualizações do Windows (operação longa).",
                1800, true),
            ["security_status"] = new("Segurança",
                "Use para status do Defender, firewall e TPM.",
                120, true),
            ["scheduled_task"] = new("Sistema",
                "Use para listar/executar tarefas agendadas (execução exige confirm=true).",
                120, true),
            ["shares"] = new("Sistema",
                "Use para listar os compartilhamentos de rede da máquina.",
                30, true),
            ["power_action"] = new("Sistema",
                "Use para reiniciar/desligar/bloquear a máquina — com aviso cancelável antes de executar.",
                30, true),
            ["send_notification"] = new("Comunicação",
                "Use para exibir um aviso ao usuário na máquina.",
                15, true),
            ["printer"] = new("Impressora",
                "Use para listar/instalar/remover impressoras, filas, drivers e spooler.",
                120, true),

            // ── Interação com o usuário (o timeout NÃO se aplica) ────────────
            ["ask_user"] = new("Interação",
                "Pergunta ao usuário e AGUARDA a resposta — o timeout não se aplica (só o botão Parar interrompe).",
                0, false),
            ["capture_screenshot"] = new("Interação",
                "Captura a tela com autorização do usuário — aguarda a decisão, então o timeout não se aplica.",
                0, false),
            ["read_file"] = new("Arquivos",
                "Lê um arquivo de texto com autorização obrigatória do usuário — aguarda a decisão, então o timeout não se aplica.",
                0, false),
            ["list_open_windows"] = new("Interação",
                "Use ANTES de capture_screenshot(mode=window) para escolher a janela alvo.",
                30, true),
            ["screenshot_permission"] = new("Interação",
                "Use quando capture_screenshot falhar por falta de autorização, para orientar o usuário.",
                15, true),

            // ── Memória local do AGENTE (anotações no computador) ────────────
            ["memory_list"] = new("Memória (agente)",
                "Lista as anotações locais gravadas no computador (memória do agente). Diferente de memory.search (histórico de conversas no servidor).",
                15, true),
            ["memory_create"] = new("Memória (agente)",
                "Grava uma anotação local persistente no computador do usuário.",
                15, true),
            ["memory_delete"] = new("Memória (agente)",
                "Remove uma anotação local pelo ID.",
                15, true),

            // ── Chamados ─────────────────────────────────────────────────────
            ["list_tickets"] = new("Chamados",
                "Use SEMPRE antes de create_ticket para evitar chamado duplicado.",
                30, true),
            ["get_ticket_details"] = new("Chamados",
                "Use para ler o texto completo de um chamado.",
                30, true),
            ["add_ticket_comment"] = new("Chamados",
                "Use para complementar um chamado existente (comentário público).",
                30, true),
            ["list_ticket_templates"] = new("Chamados",
                "Use antes de abrir chamado para conhecer os modelos disponíveis.",
                30, true),
            ["list_departments"] = new("Chamados",
                "Use para escolher o departamento responsável (obrigatório no create_ticket).",
                30, true),
            ["get_department_fields"] = new("Chamados",
                "Use após escolher o departamento para conhecer os campos personalizados obrigatórios.",
                30, true),
            ["create_ticket"] = new("Chamados",
                "Use após esgotar a solução ou quando o usuário pedir explicitamente; exige departamento.",
                90, true),
            ["close_ticket"] = new("Chamados",
                "Use quando o usuário confirmar que o problema foi resolvido.",
                60, true),
            ["reopen_ticket"] = new("Chamados",
                "Use quando o problema voltar/persistir após o encerramento.",
                60, true),
            ["rate_ticket"] = new("Chamados",
                "Use para registrar a avaliação (CSAT) de um chamado encerrado.",
                30, true),

            // ── Navegação interna ────────────────────────────────────────────
            ["get_internal_navigation_routes"] = new("Navegação",
                "Use para conhecer as rotas discovery:// disponíveis no app.",
                15, true),
            ["build_internal_navigation_link"] = new("Navegação",
                "Use para montar links/cards clicáveis no chat — com parcimônia.",
                15, true),
        };

    /// <summary>Metadados da tool; fallback genérico por origem quando desconhecida.</summary>
    public static ToolMetadata For(string toolName, string? source)
    {
        if (!string.IsNullOrWhiteSpace(toolName) && Known.TryGetValue(toolName, out var known))
            return known;

        return string.Equals(source, McpToolSources.Agent, StringComparison.OrdinalIgnoreCase)
            ? new ToolMetadata("Outros (agente)", null, 120, true)
            : new ToolMetadata("Outros (servidor)", null, 30, true);
    }
}
