using Discovery.Core.Enums;

namespace Discovery.Core.Configuration;

/// <summary>
/// Mapeamento de perfis de qualidade para parâmetros concretos de captura.
/// Define FPS alvo, escala de redimensionamento e níveis de compressão por codec.
/// </summary>
public static class QualityProfileMapping
{
    /// <summary>
    /// Retorna os parâmetros de captura para um perfil de qualidade.
    /// </summary>
    /// <param name="profile">Perfil de qualidade.</param>
    /// <returns>Tupla (fps, scalePercent 1-100, jpegQuality 1-100, webpQuality 1-100).</returns>
    public static (int Fps, int ScalePercent, int JpegQuality, int WebpQuality) GetParameters(QualityProfile profile) => profile switch
    {
        QualityProfile.Ultra => (30, 100, 92, 90),
        QualityProfile.High => (15, 100, 75, 75),
        QualityProfile.Medium => (10, 75, 60, 65),
        QualityProfile.Low => (5, 50, 40, 50),
        QualityProfile.UltraLow => (2, 30, 25, 35),
        _ => (15, 100, 75, 75)
    };

    /// <summary>
    /// Retorna label descritiva para exibição na UI.
    /// </summary>
    public static string GetLabel(QualityProfile profile) => profile switch
    {
        QualityProfile.Ultra => "Ultra (30 FPS, escala 100%, JPEG 92%)",
        QualityProfile.High => "Alta (15 FPS, escala 100%, JPEG 75%)",
        QualityProfile.Medium => "Média (10 FPS, escala 75%, JPEG 60%)",
        QualityProfile.Low => "Baixa (5 FPS, escala 50%, JPEG 40%)",
        QualityProfile.UltraLow => "Mínima (2 FPS, escala 30%, JPEG 25%)",
        _ => "Desconhecido"
    };

    /// <summary>
    /// Valida se codec é compatível com o transporte.
    /// H264 só funciona com WebRTC.
    /// </summary>
    public static bool IsCodecValidForTransport(RemoteCodec codec, RemoteTransport transport) => codec switch
    {
        RemoteCodec.H264 => transport == RemoteTransport.Webrtc,
        _ => true // JPEG e WebP funcionam em qualquer transporte
    };
}
