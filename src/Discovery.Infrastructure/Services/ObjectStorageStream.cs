using System.IO.Pipelines;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Expõe o conteúdo de um objeto do storage como Stream de leitura SEM
/// bufferizar o arquivo inteiro na memória: o provider bombeia os bytes para um
/// Pipe em background e o consumidor lê o lado de leitura.
///
/// Usar em qualquer download servido a um cliente HTTP. O
/// IObjectStorageService.DownloadAsync carrega tudo em MemoryStream e derruba a
/// API por OOM em arquivos grandes (ex.: instaladores/anexos de GB).
/// </summary>
public static class ObjectStorageStream
{
    /// <summary>
    /// Abre um objeto para leitura em streaming. Lança FileNotFoundException se
    /// o objeto não existir, preservando o comportamento "404" dos controllers.
    /// </summary>
    public static async Task<Stream> OpenReadAsync(
        IObjectStorageService storage,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);

        if (!await storage.ExistsAsync(objectKey, cancellationToken))
            throw new FileNotFoundException($"Object {objectKey} not found");

        // Buffer limitado (~1 MB): o produtor para quando o consumidor não lê,
        // mantendo a memória constante independentemente do tamanho do arquivo.
        var pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 1024 * 1024,
            resumeWriterThreshold: 512 * 1024,
            useSynchronizationContext: false));

        _ = PumpAsync(storage, objectKey, pipe, cancellationToken);
        return pipe.Reader.AsStream(leaveOpen: false);
    }

    private static async Task PumpAsync(
        IObjectStorageService storage,
        string objectKey,
        Pipe pipe,
        CancellationToken cancellationToken)
    {
        try
        {
            await storage.DownloadToAsync(objectKey, pipe.Writer.AsStream(), cancellationToken);
            await pipe.Writer.CompleteAsync();
        }
        catch (Exception ex)
        {
            // Propaga a falha para o leitor (o consumidor recebe a exceção do stream).
            // Se o leitor já finalizou (cliente abortou), CompleteAsync lança — ignorado.
            try
            {
                await pipe.Writer.CompleteAsync(ex);
            }
            catch
            {
                // pipe já finalizado pelo consumidor.
            }
        }
    }
}
