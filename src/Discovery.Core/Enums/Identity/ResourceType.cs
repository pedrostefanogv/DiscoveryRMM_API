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
    Departments
}
