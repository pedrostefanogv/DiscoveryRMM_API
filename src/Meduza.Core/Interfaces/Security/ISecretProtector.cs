namespace Meduza.Core.Interfaces.Security;

public interface ISecretProtector
{
    bool IsEnabled { get; }

    bool IsProtected(string? value);

    string Protect(string plaintext);

    string Unprotect(string protectedValue);

    string UnprotectOrSelf(string? value);
}
