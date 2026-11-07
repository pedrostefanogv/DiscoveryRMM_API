namespace Discovery.Core.Enums.Identity;

public enum ResourceType
{
    Agents,
    Tickets,
    Clients,
    Sites,
    Reports,
    ServerConfig,
    ClientConfig,
    SiteConfig,
    Users,
    Automation,
    Deployment,
    KnowledgeBase,
    AiChat,
    AppStore,
    Logs,
    Dashboard,
    RemoteDebug,
    // Recursos de configuração de suporte (criados para permitir RBAC real nos
    // controllers de workflow/SLA/departamentos, que antes só exigiam login).
    Workflow,
    Sla,
    Departments,
    // Gerenciador de patches do Windows (inventário WUA, aprovações, instalação).
    Patches,
    // Sensores de monitoramento (estado + métricas + gráficos). Recurso próprio
    // para não misturar com Automation (execução) nem Logs (leitura de eventos).
    Sensors,
    // Operações sobre a sessão remota em si (hoje: Execute = preemptar a sessão de
    // OUTRO técnico). Recurso separado de Agents porque abrir a própria sessão e
    // derrubar a de um colega são privilégios de risco bem diferente.
    RemoteSession
}
