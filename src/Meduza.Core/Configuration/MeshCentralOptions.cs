namespace Meduza.Core.Configuration;

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
    /// Login token key em HEX (160 chars / 80 bytes), gerada no MeshCentral.
    /// </summary>
    public string LoginKeyHex { get; set; } = string.Empty;

    /// <summary>
    /// Dominio do MeshCentral (vazio para default domain).
    /// </summary>
    public string DomainId { get; set; } = string.Empty;

    /// <summary>
    /// Usuario do MeshCentral utilizado para abrir a sessao de embedding.
    /// </summary>
    public string EmbedUsername { get; set; } = "admin";

    /// <summary>
    /// Permite gerar URL de embedding com usuario MeshCentral informado em runtime.
    /// </summary>
    public bool AllowRuntimeUsernameOverride { get; set; } = true;

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
    /// Template da URL de instalacao do MeshCentral.
    /// Placeholders suportados: {CLIENT_ID}, {SITE_ID}, {CLIENT_NAME}, {SITE_NAME}, {GROUP_NAME}, {MEDUZA_DEPLOY_TOKEN}
    /// </summary>
    public string AgentInstallUrlTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Usuario para autenticacao no WebSocket API (control.ashx).
    /// </summary>
    public string ApiUsername { get; set; } = string.Empty;

    /// <summary>
    /// Senha para autenticacao no WebSocket API (control.ashx).
    /// </summary>
    public string ApiPassword { get; set; } = string.Empty;

    /// <summary>
    /// Ignora erros de certificado TLS (ambiente com certificado autoassinado).
    /// </summary>
    public bool IgnoreTlsErrors { get; set; } = true;

    /// <summary>
    /// Horas de validade do link de instalacao gerado pelo MeshCentral.
    /// </summary>
    public int InviteExpireHours { get; set; } = 24;

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
}
