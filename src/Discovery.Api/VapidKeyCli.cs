using Discovery.Infrastructure.Services;

namespace Discovery.Api;

/// <summary>
/// CLI local para gerar o par VAPID exigido pelo Web Push. Roda antes de
/// construir o host (nao precisa de banco/rede) e encerra o processo.
///
/// Uso: dotnet run --project src/Discovery.Api -- --generate-vapid-keys
/// </summary>
internal static class VapidKeyCli
{
    public static bool TryRun(string[] args)
    {
        if (!args.Any(arg => string.Equals(arg, "--generate-vapid-keys", StringComparison.OrdinalIgnoreCase)))
            return false;

        var keys = VapidKeyGenerator.Generate();

        Console.WriteLine("Discovery API - par de chaves VAPID (Web Push)");
        Console.WriteLine();
        Console.WriteLine("Push__VapidPublicKey=" + keys.PublicKey);
        Console.WriteLine("Push__VapidPrivateKey=" + keys.PrivateKey);
        Console.WriteLine();
        Console.WriteLine("Defina as duas variaveis de ambiente no servidor.");
        Console.WriteLine("A chave privada e um SEGREDO: nao versione. Regenerar o par invalida");
        Console.WriteLine("todas as inscricoes de navegador existentes.");
        return true;
    }
}
