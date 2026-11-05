namespace Discovery.Core.Enums;

/// <summary>
/// Codec de compressão de vídeo para o stream de tela.
/// </summary>
public enum RemoteCodec
{
    Jpeg = 0,
    WebP = 1,

    /// <summary>
    /// OBSOLETO — não selecionar em novas sessões.
    /// O transporte WebRTC (único que suportava H.264) foi removido, e o agente
    /// desativou o encoder por licença/patente AVC (ver
    /// Discovery/src/app/core/remotesession/codec_selector.go). A validação
    /// <see cref="Configuration.QualityProfileMapping.IsCodecValidForTransport"/>
    /// rejeita este valor.
    ///
    /// O VALOR NUMÉRICO (2) É PRESERVADO DE PROPÓSITO: a coluna <c>codec</c> é
    /// persistida como int e sessões/gravações históricas o utilizam.
    /// </summary>
    [Obsolete("H.264 não é suportado pelo runtime (WebRTC removido, encoder AVC desativado). Use Jpeg ou WebP. Valor mantido apenas para compatibilidade de dados históricos.")]
    H264 = 2
}
