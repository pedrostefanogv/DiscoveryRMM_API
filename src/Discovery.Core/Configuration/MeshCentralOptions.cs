namespace Discovery.Core.Configuration;

/// <summary>
/// Configuracao para gerar URLs de embedding do MeshCentral via auth token assinado.
/// </summary>
public class MeshCentralOptions
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// URL base do MeshCentral, ex: https://mesh.suaempresa.com/
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// URL publica usada para compor InstallUrl (download direto do agent).
    /// Quando vazia, reutiliza BaseUrl.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Login token key em HEX (160 chars / 80 bytes), gerada no MeshCentral.
    /// </summary>
    public string LoginKeyHex { get; set; } = string.Empty;

    /// <summary>
    /// Dominio do MeshCentral (vazio para default domain).
    /// </summary>
    public string DomainId { get; set; } = string.Empty;

    /// <summary>
    /// Tempo de sessao sugerido (minutos), apenas informativo para o caller.
    /// </summary>
    public int SuggestedSessionMinutes { get; set; } = 10;

    /// <summary>
    /// View modes permitidos para reduzir superficie de abuso.
    /// </summary>
    public int[] AllowedViewModes { get; set; } = [10, 11, 12, 13, 16];

    /// <summary>
    /// Bitmask default de UI a ocultar (header, tabs, footer, title).
    /// </summary>
    public int DefaultHideMask { get; set; } = 15;

    /// <summary>
    /// Habilita geracao de instrucoes de instalacao automatizada do agent MeshCentral.
    /// </summary>
    public bool EnableProvisioningHints { get; set; } = false;

    /// <summary>
    /// Template legado de URL de instalacao.
    /// Mantido por compatibilidade; novos fluxos devem usar InstallUrl direto (/meshagents).
    /// </summary>
    public string AgentInstallUrlTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Id de arquitetura do MeshAgent para gerar InstallUrl direto.
    /// Default: 4 (Windows x64).
    /// </summary>
    public int AgentDownloadArchitectureId { get; set; } = 4;

    /// <summary>
    /// Install flags enviados para /meshagents.
    /// 0 = default, 1 = interactive only, 2 = background only.
    /// </summary>
    public int AgentDownloadInstallFlags { get; set; } = 0;

    /// <summary>
    /// Ignora erros de certificado TLS (ambiente com certificado autoassinado).
    /// </summary>
    public bool IgnoreTlsErrors { get; set; } = true;

    /// <summary>
    /// Horas de validade do link de instalacao gerado pelo MeshCentral.
    /// </summary>
    public int InviteExpireHours { get; set; } = 24;

    /// <summary>
    /// Timeout em segundos para operacoes WebSocket no control.ashx.
    /// </summary>
    public int ApiTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Modo padrao de instalacao do agent: background ou interactive.
    /// </summary>
    public string InstallExecutionMode { get; set; } = "background";

    /// <summary>
    /// Habilita sincronizacao de identidades de usuarios com MeshCentral.
    /// </summary>
    public bool IdentitySyncEnabled { get; set; } = false;

    /// <summary>
    /// Direitos padrao ao vincular usuario em grupo de dispositivo (0 = minimo).
    /// </summary>
    public int IdentitySyncDefaultMeshRights { get; set; } = 0;

    /// <summary>
    /// Habilita o calculo de direitos Mesh a partir das politicas da aplicacao.
    /// </summary>
    public bool IdentitySyncPolicyEnabled { get; set; } = false;

    /// <summary>
    /// Quando true, calcula a policy efetiva e registra logs, mas nao aplica rights no Mesh.
    /// </summary>
    public bool IdentitySyncPolicyDryRun { get; set; } = true;

    /// <summary>
    /// Perfil fallback quando nao houver mapeamento explicito de role.
    /// </summary>
    public string IdentitySyncPolicyDefaultProfile { get; set; } = "viewer";

    /// <summary>
    /// Quando true, remove bits criticos se a role da aplicacao nao tiver permissao equivalente.
    /// </summary>
    public bool IdentitySyncPolicyStrictMode { get; set; } = true;

    /// <summary>
    /// Perfis de rights por nome. Ex: viewer=8448, operator=29688, admin=-1.
    /// </summary>
    public Dictionary<string, int> IdentitySyncPolicyProfiles { get; set; } = new()
    {
        ["viewer"] = 256 + 8192,
        ["operator"] = 8 + 16 + 32 + 64 + 128 + 256 + 4096 + 8192 + 16384 + 32768,
        ["admin"] = -1
    };

    /// <summary>
    /// Mapeia role da aplicacao para perfil Mesh. Ex: Operator -> operator.
    /// </summary>
    public Dictionary<string, string> IdentitySyncRoleProfiles { get; set; } = new();

    /// <summary>
    /// Overrides de rights por nome da role da aplicacao.
    /// </summary>
    public Dictionary<string, int> IdentitySyncRoleRightsOverrides { get; set; } = new();

    /// <summary>
    /// Habilita reconciliacao periodica automatica do sync de identidades.
    /// </summary>
    public bool IdentitySyncReconciliationEnabled { get; set; } = false;

    /// <summary>
    /// Executa reconciliacao com aplicacao de alteracoes remotas. Se false, executa apenas dry-run.
    /// </summary>
    public bool IdentitySyncReconciliationApplyChanges { get; set; } = false;

    /// <summary>
    /// Intervalo da reconciliacao automatica em minutos.
    /// </summary>
    public int IdentitySyncReconciliationIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Atraso inicial, em segundos, antes da primeira execucao da reconciliacao.
    /// </summary>
    public int IdentitySyncReconciliationStartupDelaySeconds { get; set; } = 30;

    /// <summary>
    /// Habilita reconciliacao periodica de policy de grupos por site.
    /// </summary>
    public bool GroupPolicyReconciliationEnabled { get; set; } = false;

    /// <summary>
    /// Executa reconciliacao de policy de grupos com aplicacao remota. Se false, apenas dry-run.
    /// </summary>
    public bool GroupPolicyReconciliationApplyChanges { get; set; } = false;

    /// <summary>
    /// Intervalo, em minutos, da reconciliacao periodica de policy de grupos.
    /// </summary>
    public int GroupPolicyReconciliationIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Atraso inicial, em segundos, antes da primeira reconciliacao de policy de grupos.
    /// </summary>
    public int GroupPolicyReconciliationStartupDelaySeconds { get; set; } = 30;
}
