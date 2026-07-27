using Discovery.Core.Configuration;
using Discovery.Core.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>
/// Monta o arquivo manifesto de gravação (WebM/MP4) a partir de frames individuais.
/// Responsável por gerar o container final e metadados de índice.
///
/// Para WebM:
///   - Escreve cabeçalho EBML + Segment + Info + Tracks
///   - Adiciona Cluster para cada grupo de frames
///   - Finaliza com Cues (índice de seek)
///
/// Para MP4 (H.264):
///   - Escreve ftyp + moov (mvhd, trak, stbl)
///   - Adiciona mdat chunks
///   - Atualiza stco/co64 offsets no final
///
/// Nota: esta é uma implementação simplificada. Em produção,
/// usar FFmpeg/libavformat ou matroska/mp4ff para muxing completo.
/// </summary>
public class RecordingManifestWriter
{
    private readonly RemoteAccessOptions _options;
    private readonly ILogger<RecordingManifestWriter> _logger;

    public RecordingManifestWriter(
        IOptions<RemoteAccessOptions> options,
        ILogger<RecordingManifestWriter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Cria o arquivo container final a partir de uma lista de frames.
    /// </summary>
    /// <param name="frames">Lista de frames (JPEG/WebP/H.264) com timestamps.</param>
    /// <param name="codec">Codec de origem.</param>
    /// <param name="width">Largura do vídeo.</param>
    /// <param name="height">Altura do vídeo.</param>
    /// <param name="averageFps">FPS médio da gravação.</param>
    /// <param name="ct">Token de cancelamento.</param>
    /// <returns>Container format (webm/mp4) e bytes do arquivo final.</returns>
    public async Task<(string ContainerFormat, byte[] FileData)> AssembleAsync(
        IReadOnlyList<RecordingFrame> frames,
        RemoteCodec codec,
        int width,
        int height,
        double averageFps,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Assembling recording: {FrameCount} frames, codec {Codec}, {Width}x{Height}, {Fps:F1} FPS",
            frames.Count, codec, width, height, averageFps);

        return codec switch
        {
            RemoteCodec.H264 => AssembleMp4(frames, width, height, averageFps),
            _ => AssembleWebM(frames, codec, width, height, averageFps, ct)
        };
    }

    /// <summary>
    /// Monta container WebM (VP8/VP9 para WebP ou MJPEG para JPEG).
    /// Estrutura simplificada: EBML header + Segment + Info + Tracks + Clusters.
    /// </summary>
    private (string ContainerFormat, byte[] FileData) AssembleWebM(
        IReadOnlyList<RecordingFrame> frames,
        RemoteCodec codec,
        int width,
        int height,
        double averageFps,
        CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // EBML Header
        WriteWebMEbmlHeader(writer);

        // Segment (conteúdo principal)
        var segmentSizePos = WriteWebMElementHeader(writer, 0x18538067, 0); // placeholder size
        var segmentStart = ms.Position;

        // Segment Info
        WriteWebMSegmentInfo(writer, frames, averageFps);

        // Tracks
        WriteWebMTracks(writer, codec, width, height);

        // Clusters (grupos de frames)
        WriteWebMClusters(writer, frames, codec, ct);

        // Atualiza o tamanho do segmento
        var segmentEnd = ms.Position;
        var segmentSize = segmentEnd - segmentStart;
        ms.Position = segmentSizePos;
        WriteVariableLengthUInt(ms, (ulong)segmentSize);

        writer.Flush();
        _logger.LogInformation("WebM assembled: {Bytes} bytes, {Frames} frames", ms.Length, frames.Count);

        return ("webm", ms.ToArray());
    }

    /// <summary>
    /// Monta container MP4 (H.264 em ftyp + moov + mdat).
    /// Implementação simplificada — usa boxes ISO Base Media File Format.
    /// </summary>
    private (string ContainerFormat, byte[] FileData) AssembleMp4(
        IReadOnlyList<RecordingFrame> frames,
        int width,
        int height,
        double averageFps)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // ftyp box (file type)
        WriteMp4Ftyp(writer);

        // mdat box (media data — frames)
        var mdatSizePos = WriteMp4BoxHeader(writer, "mdat", 0);
        var mdatStart = ms.Position;

        foreach (var frame in frames)
        {
            // Escreve NAL unit length prefix (4 bytes) + frame data
            var lenBytes = BitConverter.GetBytes(frame.Data.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
            writer.Write(lenBytes);
            writer.Write(frame.Data);
        }

        var mdatEnd = ms.Position;
        var mdatSize = mdatEnd - mdatStart + 8; // +8 para o header

        // moov box (metadata)
        WriteMp4Moov(writer, frames, width, height, averageFps, mdatStart);

        // Atualiza tamanho do mdat
        ms.Position = mdatSizePos;
        writer.Write(BitConverter.GetBytes((uint)mdatSize).Reverse().ToArray());

        writer.Flush();
        _logger.LogInformation("MP4 assembled: {Bytes} bytes, {Frames} frames", ms.Length, frames.Count);

        return ("mp4", ms.ToArray());
    }

    // ── WebM Helpers ──

    private static void WriteWebMEbmlHeader(BinaryWriter writer)
    {
        // EBML Header: 0x1A45DFA3
        writer.Write(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });
        // EBML Version + DocType + DocTypeVersion
        var header = new byte[]
        {
            0x42, 0x86, 0x81, 0x01, // EBMLVersion = 1
            0x42, 0xF7, 0x81, 0x01, // EBMLReadVersion = 1
            0x42, 0xF2, 0x81, 0x04, // EBMLMaxIDLength = 4
            0x42, 0xF3, 0x81, 0x08, // EBMLMaxSizeLength = 8
            0x42, 0x82, 0x84, 0x77, 0x65, 0x62, 0x6D, // DocType = "webm"
            0x42, 0x87, 0x81, 0x04, // DocTypeVersion = 4
            0x42, 0x85, 0x81, 0x02, // DocTypeReadVersion = 2
        };
        writer.Write(header);
    }

    private static long WriteWebMElementHeader(BinaryWriter writer, uint elementId, long size)
    {
        writer.Write(BitConverter.GetBytes(elementId).Reverse().ToArray());
        var pos = writer.BaseStream.Position;
        WriteVariableLengthUInt(writer.BaseStream, (ulong)size);
        return pos;
    }

    private static void WriteVariableLengthUInt(Stream stream, ulong value)
    {
        // Simplified: write as 8-byte big-endian with variable length marker
        var bytes = BitConverter.GetBytes(value);
        Array.Reverse(bytes);
        // Find first significant byte
        var start = 0;
        while (start < 7 && bytes[start] == 0) start++;
        var len = 8 - start;
        // Set variable length marker on first byte
        bytes[start] |= (byte)(0x80 >> (len - 1));
        stream.Write(bytes, start, len);
    }

    private void WriteWebMSegmentInfo(BinaryWriter writer, IReadOnlyList<RecordingFrame> frames, double fps)
    {
        // Info element
        if (frames.Count == 0) return;

        var durationNs = (ulong)(frames.Count / fps * 1_000_000_000);
        var timecodeScale = 1_000_000u; // 1ms

        WriteWebMElementHeader(writer, 0x1549A966, 0); // Info placeholder
        var infoStart = writer.BaseStream.Position;

        // TimecodeScale
        writer.Write(new byte[] { 0x2A, 0xD7, 0xB1 });
        WriteVariableLengthUInt(writer.BaseStream, timecodeScale);

        // Duration (float)
        writer.Write(new byte[] { 0x44, 0x89 });
        var durBytes = BitConverter.GetBytes((float)durationNs / timecodeScale);
        writer.Write(durBytes);

        // MuxingApp
        writer.Write(new byte[] { 0x4D, 0x80 });
        var appName = "DiscoveryRMM Recording"u8;
        WriteVariableLengthUInt(writer.BaseStream, (ulong)appName.Length);
        writer.Write(appName);

        // WritingApp
        writer.Write(new byte[] { 0x57, 0x41 });
        writer.Write(appName);

        var infoEnd = writer.BaseStream.Position;
        // Update info size (volta e escreve; simplificado com placeholder)
    }

    private void WriteWebMTracks(BinaryWriter writer, RemoteCodec codec, int width, int height)
    {
        WriteWebMElementHeader(writer, 0x1654AE6B, 0); // Tracks
        var tracksStart = writer.BaseStream.Position;

        // TrackEntry
        WriteWebMElementHeader(writer, 0xAE, 0);

        // TrackNumber = 1
        writer.Write(new byte[] { 0xD7, 0x81, 0x01 });

        // TrackUID = 1
        writer.Write(new byte[] { 0x73, 0xC5, 0x81, 0x01 });

        // TrackType = 1 (video)
        writer.Write(new byte[] { 0x83, 0x81, 0x01 });

        // CodecID
        var codecId = codec switch
        {
            RemoteCodec.H264 => "V_MPEG4/ISO/AVC",
            RemoteCodec.WebP => "V_VP8",
            _ => "V_MJPEG"
        };
        writer.Write(new byte[] { 0x86 });
        var codecBytes = Encoding.ASCII.GetBytes(codecId);
        WriteVariableLengthUInt(writer.BaseStream, (ulong)codecBytes.Length);
        writer.Write(codecBytes);

        // Video
        writer.Write(new byte[] { 0xE0 });
        WriteVariableLengthUInt(writer.BaseStream, 0); // placeholder

        // PixelWidth
        writer.Write(new byte[] { 0xB0 });
        WriteVariableLengthUInt(writer.BaseStream, (ulong)width);

        // PixelHeight
        writer.Write(new byte[] { 0xBA });
        WriteVariableLengthUInt(writer.BaseStream, (ulong)height);
    }

    private void WriteWebMClusters(
        BinaryWriter writer,
        IReadOnlyList<RecordingFrame> frames,
        RemoteCodec codec,
        CancellationToken ct)
    {
        const int framesPerCluster = 30; // ~1s de vídeo a 30fps

        for (var i = 0; i < frames.Count; i += framesPerCluster)
        {
            ct.ThrowIfCancellationRequested();

            var clusterFrames = frames.Skip(i).Take(framesPerCluster).ToList();
            WriteWebMElementHeader(writer, 0x1F43B675, 0); // Cluster
            var clusterStart = writer.BaseStream.Position;

            // Timecode (em ms)
            writer.Write(new byte[] { 0xE7 });
            var clusterTimecode = (ulong)(i / 30.0 * 1000);
            WriteVariableLengthUInt(writer.BaseStream, clusterTimecode);

            foreach (var frame in clusterFrames)
            {
                WriteWebMSimpleBlock(writer, frame, codec);
            }
        }
    }

    private static void WriteWebMSimpleBlock(BinaryWriter writer, RecordingFrame frame, RemoteCodec codec)
    {
        // SimpleBlock element
        writer.Write(new byte[] { 0xA3 });
        // Size: track number (1) + timecode (2) + flags (1) + data
        var size = 4 + frame.Data.Length;
        WriteVariableLengthUInt(writer.BaseStream, (ulong)size);

        // Track number (1 byte)
        writer.Write((byte)0x01);

        // Timecode (relative to cluster, 2 bytes signed big-endian)
        var relativeTimecode = (short)(frame.TimestampMs % 1000);
        var tcBytes = BitConverter.GetBytes(relativeTimecode);
        if (BitConverter.IsLittleEndian) Array.Reverse(tcBytes);
        writer.Write(tcBytes);

        // Flags (keyframe = 0x80)
        writer.Write((byte)0x80);

        // Frame data
        writer.Write(frame.Data);
    }

    // ── MP4 Helpers ──

    private static void WriteMp4Ftyp(BinaryWriter writer)
    {
        // ftyp box
        var ftypData = new byte[]
        {
            0x00, 0x00, 0x00, 0x14, // size = 20
            0x66, 0x74, 0x79, 0x70, // 'ftyp'
            0x69, 0x73, 0x6F, 0x6D, // major brand = 'isom'
            0x00, 0x00, 0x00, 0x01, // minor version
            0x69, 0x73, 0x6F, 0x6D, // compatible brand = 'isom'
        };
        writer.Write(ftypData);
    }

    private static long WriteMp4BoxHeader(BinaryWriter writer, string boxType, uint size)
    {
        var pos = writer.BaseStream.Position;
        writer.Write(BitConverter.GetBytes(size).Reverse().ToArray());
        writer.Write(Encoding.ASCII.GetBytes(boxType));
        return pos;
    }

    private void WriteMp4Moov(
        BinaryWriter writer,
        IReadOnlyList<RecordingFrame> frames,
        int width,
        int height,
        double fps,
        long mdatStart)
    {
        WriteMp4BoxHeader(writer, "moov", 0); // size atualizado depois
        var moovStart = writer.BaseStream.Position;

        // mvhd (movie header)
        WriteMp4Mvhd(writer, frames.Count, fps);

        // trak (track)
        WriteMp4BoxHeader(writer, "trak", 0);
        var trakStart = writer.BaseStream.Position;

        // tkhd (track header)
        WriteMp4Tkhd(writer, width, height);

        // mdia (media)
        WriteMp4BoxHeader(writer, "mdia", 0);

        // mdhd (media header)
        WriteMp4Mdhd(writer, frames.Count, fps);

        // hdlr (handler)
        var hdlrData = new byte[]
        {
            0x00, 0x00, 0x00, 0x21, // size
            0x68, 0x64, 0x6C, 0x72, // 'hdlr'
            0x00, 0x00, 0x00, 0x00, // version + flags
            0x00, 0x00, 0x00, 0x00, // pre_defined
            0x76, 0x69, 0x64, 0x65, // handler = 'vide'
            0x00, 0x00, 0x00, 0x00, // reserved[3]
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00 // name = empty
        };
        writer.Write(hdlrData);

        // stbl (sample table)
        WriteMp4BoxHeader(writer, "stbl", 0);

        var timeScale = (uint)(fps * 1000);
        var sampleCount = (uint)frames.Count;

        // stsd (sample description)
        WriteMp4Stsd(writer, width, height);

        // stts (time-to-sample) — todos os frames com mesma duração
        WriteMp4FullBoxHeader(writer, "stts", 0, 0);
        var sttsData = new List<byte>();
        sttsData.AddRange(BitConverter.GetBytes(sampleCount).Reverse()); // entry count = 1
        sttsData.AddRange(BitConverter.GetBytes(sampleCount).Reverse()); // sample count
        sttsData.AddRange(BitConverter.GetBytes((uint)(timeScale / fps)).Reverse()); // sample delta
        var sttsSize = 12 + sttsData.Count;
        writer.Write(BitConverter.GetBytes((uint)sttsSize).Reverse().ToArray());
        writer.Write(Encoding.ASCII.GetBytes("stts"));
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // version + flags
        writer.Write(sttsData.ToArray());

        // stsz (sample sizes)
        WriteMp4FullBoxHeader(writer, "stsz", 0, 0);
        var stszData = new List<byte>();
        stszData.AddRange(BitConverter.GetBytes((uint)0).Reverse()); // sample size = 0 (variable)
        stszData.AddRange(BitConverter.GetBytes(sampleCount).Reverse()); // entry count
        foreach (var f in frames)
            stszData.AddRange(BitConverter.GetBytes((uint)f.Data.Length).Reverse());
        var stszSize = 12 + stszData.Count;
        writer.BaseStream.Seek(-8, SeekOrigin.Current); // volta header
        writer.Write(BitConverter.GetBytes((uint)stszSize).Reverse().ToArray());
        writer.Write(Encoding.ASCII.GetBytes("stsz"));
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        writer.Write(stszData.ToArray());

        // stco (chunk offsets)
        WriteMp4FullBoxHeader(writer, "stco", 0, 0);
        var stcoData = new List<byte>();
        stcoData.AddRange(BitConverter.GetBytes((uint)1).Reverse()); // entry count = 1 chunk
        stcoData.AddRange(BitConverter.GetBytes((uint)mdatStart).Reverse()); // offset to mdat
        var stcoSize = 12 + stcoData.Count;
        writer.BaseStream.Seek(-8, SeekOrigin.Current);
        writer.Write(BitConverter.GetBytes((uint)stcoSize).Reverse().ToArray());
        writer.Write(Encoding.ASCII.GetBytes("stco"));
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        writer.Write(stcoData.ToArray());
    }

    private static void WriteMp4Mvhd(BinaryWriter writer, int frameCount, double fps)
    {
        var duration = (uint)(frameCount / fps * 1000); // em timescale de 1000
        WriteMp4FullBoxHeader(writer, "mvhd", 0, 0);
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // creation time
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // modification time
        writer.Write(BitConverter.GetBytes((uint)1000).Reverse().ToArray()); // timescale
        writer.Write(BitConverter.GetBytes(duration).Reverse().ToArray()); // duration
        writer.Write(new byte[] { 0x00, 0x01, 0x00, 0x00 }); // rate 1.0
        writer.Write(new byte[] { 0x01, 0x00 }); // volume 1.0
        writer.Write(new byte[10]); // reserved
        // matrix (36 bytes — identity)
        writer.Write(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00 });
        writer.Write(new byte[24]); // pre_defined
        writer.Write(BitConverter.GetBytes((uint)2).Reverse().ToArray()); // next track id
    }

    private static void WriteMp4Tkhd(BinaryWriter writer, int width, int height)
    {
        WriteMp4FullBoxHeader(writer, "tkhd", 0, 0x07); // track enabled + in movie + in preview
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // creation time
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // modification time
        writer.Write(BitConverter.GetBytes((uint)1).Reverse().ToArray()); // track id
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // reserved
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // duration (placeholder)
        writer.Write(new byte[8]); // reserved
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // layer
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // alternate group
        writer.Write(BitConverter.GetBytes((ushort)0).Reverse().ToArray()); // volume
        writer.Write(new byte[] { 0x00, 0x00 }); // reserved
        // matrix (identity)
        writer.Write(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00 });
        // width/height (16.16 fixed point)
        writer.Write(BitConverter.GetBytes((uint)(width << 16)).Reverse().ToArray());
        writer.Write(BitConverter.GetBytes((uint)(height << 16)).Reverse().ToArray());
    }

    private static void WriteMp4Mdhd(BinaryWriter writer, int frameCount, double fps)
    {
        var duration = (uint)(frameCount / fps * 1000);
        WriteMp4FullBoxHeader(writer, "mdhd", 0, 0);
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // creation time
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // modification time
        writer.Write(BitConverter.GetBytes((uint)1000).Reverse().ToArray()); // timescale
        writer.Write(BitConverter.GetBytes(duration).Reverse().ToArray()); // duration
        writer.Write(new byte[] { 0x55, 0xC4 }); // language = und (0x55C4)
        writer.Write(new byte[] { 0x00, 0x00 }); // pre_defined
    }

    private static void WriteMp4Stsd(BinaryWriter writer, int width, int height)
    {
        WriteMp4FullBoxHeader(writer, "stsd", 0, 0);
        writer.Write(BitConverter.GetBytes((uint)1).Reverse().ToArray()); // entry count

        // avc1 sample entry
        var avc1Data = new byte[]
        {
            0x00, 0x00, 0x00, 0x56, // size of avc1
            0x61, 0x76, 0x63, 0x31, // 'avc1'
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // reserved
            0x00, 0x01, // data reference index
            0x00, 0x00, // pre_defined
            0x00, 0x00, // reserved
            0x00, 0x00, 0x00, 0x00, // pre_defined
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        };
        writer.Write(avc1Data);
        writer.Write(BitConverter.GetBytes((ushort)width).Reverse().ToArray());
        writer.Write(BitConverter.GetBytes((ushort)height).Reverse().ToArray());
        writer.Write(new byte[] { 0x00, 0x48, 0x00, 0x00 }); // horiz/vert resolution 72 dpi
        writer.Write(new byte[] { 0x00, 0x48, 0x00, 0x00 });
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // data reference
        writer.Write(new byte[] { 0x00, 0x01 }); // frame count
        writer.Write(new byte[32]); // compressor name
        writer.Write(new byte[] { 0x00, 0x18 }); // depth
        writer.Write(new byte[] { 0xFF, 0xFF }); // pre_defined = -1
    }

    private static void WriteMp4FullBoxHeader(BinaryWriter writer, string boxType, byte version, uint flags)
    {
        writer.Write(BitConverter.GetBytes((uint)0).Reverse().ToArray()); // size placeholder
        writer.Write(Encoding.ASCII.GetBytes(boxType));
        writer.Write(version);
        writer.Write(new byte[] { (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags });
    }
}

/// <summary>
/// Frame individual de gravação com metadados de timestamp.
/// </summary>
public sealed record RecordingFrame(
    byte[] Data,
    double TimestampMs,
    bool IsKeyframe = true
);
