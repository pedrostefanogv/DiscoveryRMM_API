namespace Discovery.Core.Enums;

/// <summary>
/// Restrições de uma sessão de acesso remoto, aplicadas no SERVIDOR.
///
/// Inspirado nos bits <c>MESHRIGHT_*</c> do MeshCentral (agentes podem ser
/// concedidos com view-only, sem terminal, sem arquivos, input limitado), que são
/// a granularidade mais fina encontrada nos RMMs comparados. O valor aqui é
/// direto para clientes com exigência de LGPD: "pode ver, não pode clicar" é um
/// pedido comum e, sem isto, a única opção era conceder controle total.
///
/// <b>Imposto como ACL NATS</b>: a restrição não é dica de UI. Os subjects de
/// input/terminal/arquivos simplesmente não entram na credencial do viewer, então
/// o próprio broker recusa a operação (ver RemoteSessionViewerPermissions).
///
/// Os valores foram escolhidos espelhando os do MeshCentral para facilitar a
/// comparação, mas são independentes — o que importa é o que o Discovery aplica.
/// </summary>
[Flags]
public enum RemoteSessionRights
{
    /// <summary>Sem restrições (default: comportamento anterior, controle total).</summary>
    None = 0,

    /// <summary>Somente visualização: sem input de teclado/mouse.</summary>
    ViewOnly = 1,

    /// <summary>Sem terminal remoto.</summary>
    NoTerminal = 2,

    /// <summary>Sem transferência/navegação de arquivos.</summary>
    NoFiles = 4,

    /// <summary>Input limitado (mouse move/clique bloqueados, teclado permitido).</summary>
    LimitedInput = 8,

    /// <summary>Sem acesso à área de trabalho (apenas processos/serviços).</summary>
    NoDesktop = 16,

    /// <summary>Sem eventos de sistema da sessão (auditoria reduzida para o operador).</summary>
    NoEvents = 32
}
