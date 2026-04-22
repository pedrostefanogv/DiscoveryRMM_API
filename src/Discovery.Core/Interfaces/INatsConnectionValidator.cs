using System.Threading;
using System.Threading.Tasks;

namespace Discovery.Core.Interfaces;

public interface INatsConnectionValidator
{
    Task<(bool IsValid, string[] Errors)> ValidateConnectionAsync(
        string url,
        string? user,
        string? password,
        CancellationToken cancellationToken);
}
