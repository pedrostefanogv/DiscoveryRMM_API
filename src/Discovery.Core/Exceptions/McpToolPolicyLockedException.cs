namespace Discovery.Core.Exceptions;

/// <summary>
/// Lançada ao tentar sobrescrever a política de uma MCP tool bloqueada
/// (Locked=true) em um escopo mais genérico — o "bloqueio de herança" da
/// governança. O controller traduz para HTTP 409.
/// </summary>
public class McpToolPolicyLockedException(string toolName)
    : InvalidOperationException($"A ferramenta \"{toolName}\" está bloqueada para herança em um nível superior; remova o bloqueio antes de alterá-la neste escopo.")
{
    public string ToolName { get; } = toolName;
}
