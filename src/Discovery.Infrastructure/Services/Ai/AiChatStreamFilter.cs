using System.Text;
using Discovery.Core.ValueObjects;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Filtro de streaming em tempo real para tokens SSE:
/// - Suprime blocos ```a2ui ... ``` e marcações internas (&lt;｜DSML｜&gt;, &lt;|&lt;DSML|&gt;, &lt;invoke&gt;)
///   antes de emitirem bytes ao cliente.
/// - Aplica DLP guardrails (API keys, JWTs, CPFs válidos, cartões) em segmentos
///   seguros antes de enviar pela rede.
/// </summary>
public sealed class AiChatStreamFilter
{
    private enum State
    {
        Normal,
        A2uiFence,
        DsmlBlock,
        InvokeBlock,
    }

    private readonly AIIntegrationSettings _settings;
    private readonly StringBuilder _buf = new();
    private State _state = State.Normal;

    public AiChatStreamFilter(AIIntegrationSettings settings)
    {
        _settings = settings;
    }

    public string Push(string? chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return string.Empty;

        _buf.Append(chunk);
        return Drain(final: false);
    }

    public string Flush() => Drain(final: true);

    private string Drain(bool final)
    {
        var output = new StringBuilder();

        while (_buf.Length > 0)
        {
            var text = _buf.ToString();

            if (_state == State.Normal)
            {
                var nextIdx = FindEarliestMarker(text, out var markerState, out var markerLen);
                if (nextIdx >= 0)
                {
                    if (nextIdx > 0)
                    {
                        output.Append(AiChatGuardrails.ApplyOutputGuardrails(text[..nextIdx], _settings));
                    }
                    _buf.Remove(0, nextIdx + markerLen);
                    _state = markerState;
                    continue;
                }

                if (final)
                {
                    output.Append(AiChatGuardrails.ApplyOutputGuardrails(text, _settings));
                    _buf.Clear();
                    break;
                }

                // Retém cauda curta para detectar marcadores ou segredos divididos entre tokens.
                const int keepTail = 32;
                if (text.Length <= keepTail)
                    break;

                // Corta em espaço/quebra de linha para não partir palavras/segredos no meio.
                var safeLen = text.Length - keepTail;
                var lastWs = text.LastIndexOfAny([' ', '\n', '\r', '\t'], safeLen - 1);
                if (lastWs <= 0)
                    break;

                var emitLen = lastWs + 1;
                output.Append(AiChatGuardrails.ApplyOutputGuardrails(text[..emitLen], _settings));
                _buf.Remove(0, emitLen);
                break;
            }
            else if (_state == State.A2uiFence)
            {
                var closeIdx = text.IndexOf("```", StringComparison.Ordinal);
                if (closeIdx >= 0)
                {
                    _buf.Remove(0, closeIdx + 3);
                    _state = State.Normal;
                    continue;
                }
                if (final) { _buf.Clear(); break; }
                TrimBufferTail(3);
                break;
            }
            else if (_state == State.DsmlBlock)
            {
                var closeIdx = text.IndexOf("</", StringComparison.Ordinal);
                if (closeIdx >= 0)
                {
                    var gt = text.IndexOf('>', closeIdx);
                    if (gt >= 0)
                    {
                        _buf.Remove(0, gt + 1);
                        _state = State.Normal;
                        continue;
                    }
                }
                if (final) { _buf.Clear(); break; }
                TrimBufferTail(16);
                break;
            }
            else if (_state == State.InvokeBlock)
            {
                var closeIdx = text.IndexOf("</invoke>", StringComparison.OrdinalIgnoreCase);
                if (closeIdx >= 0)
                {
                    _buf.Remove(0, closeIdx + "</invoke>".Length);
                    _state = State.Normal;
                    continue;
                }
                if (final) { _buf.Clear(); break; }
                TrimBufferTail(9);
                break;
            }
        }

        return output.ToString();
    }

    private void TrimBufferTail(int keep)
    {
        if (_buf.Length > keep)
            _buf.Remove(0, _buf.Length - keep);
    }

    private static int FindEarliestMarker(string text, out State state, out int markerLen)
    {
        var bestIdx = -1;
        state = State.Normal;
        markerLen = 0;

        CheckMarker(text.IndexOf("```a2ui", StringComparison.OrdinalIgnoreCase), 7, State.A2uiFence, ref bestIdx, ref state, ref markerLen);
        CheckMarker(text.IndexOf("<｜DSML｜", StringComparison.Ordinal), 8, State.DsmlBlock, ref bestIdx, ref state, ref markerLen);
        CheckMarker(text.IndexOf("<|DSML|", StringComparison.Ordinal), 7, State.DsmlBlock, ref bestIdx, ref state, ref markerLen);
        CheckMarker(text.IndexOf("<invoke ", StringComparison.OrdinalIgnoreCase), 8, State.InvokeBlock, ref bestIdx, ref state, ref markerLen);

        return bestIdx;
    }

    private static void CheckMarker(int idx, int len, State candidateState, ref int bestIdx, ref State bestState, ref int bestLen)
    {
        if (idx >= 0 && (bestIdx < 0 || idx < bestIdx))
        {
            bestIdx = idx;
            bestState = candidateState;
            bestLen = len;
        }
    }
}
