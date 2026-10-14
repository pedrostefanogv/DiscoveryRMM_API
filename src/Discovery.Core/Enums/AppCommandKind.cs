namespace Discovery.Core.Enums;

/// <summary>
/// Forma de execução do comando de instalação/desinstalação de um pacote custom.
/// Retrocompatível: registros gravados antes desta coluna ficam em <see cref="Command"/>.
/// </summary>
public enum AppCommandKind
{
    /// <summary>Comando único, executado via <c>cmd /C</c> (comportamento original).</summary>
    Command = 0,

    /// <summary>Script CMD/BAT: o texto é gravado num <c>.cmd</c> temporário e executado.</summary>
    CmdScript = 1,

    /// <summary>Script PowerShell: o texto é gravado num <c>.ps1</c> temporário e executado.</summary>
    PowerShellScript = 2
}
