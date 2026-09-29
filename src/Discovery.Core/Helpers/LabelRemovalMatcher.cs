using System.Text.RegularExpressions;
using Discovery.Core.Enums;

namespace Discovery.Core.Helpers;

/// <summary>
/// Matcher do alvo de uma regra no modo <see cref="AgentLabelApplyMode.Remove"/>.
///
/// A regex e compilada UMA vez (na preparacao das regras) e executada com timeout,
/// igual ao avaliador de expressoes — evita custo por agente e ReDoS.
/// Use <see cref="TryCreate"/> para nao lancar com padrao invalido.
/// </summary>
public sealed class LabelRemovalMatcher
{
    /// <summary>Mesmo limite das expressoes (validation.ts / AgentLabelExpressionValidator).</summary>
    public const int MaxPatternLength = 256;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private readonly Regex? _regex;

    private LabelRemovalMatcher(string value, AgentLabelLabelMatch match, Regex? regex)
    {
        Value = value;
        Match = match;
        _regex = regex;
    }

    public string Value { get; }

    public AgentLabelLabelMatch Match { get; }

    public static bool TryCreate(string value, AgentLabelLabelMatch match, out LabelRemovalMatcher? matcher)
    {
        matcher = null;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();

        if (match == AgentLabelLabelMatch.Regex)
        {
            if (trimmed.Length > MaxPatternLength)
                return false;

            try
            {
                var regex = new Regex(
                    trimmed,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    RegexTimeout);

                matcher = new LabelRemovalMatcher(trimmed, match, regex);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        matcher = new LabelRemovalMatcher(trimmed, match, regex: null);
        return true;
    }

    public bool Matches(string label)
    {
        if (string.IsNullOrEmpty(label))
            return false;

        return Match switch
        {
            AgentLabelLabelMatch.Prefix => label.StartsWith(Value, StringComparison.OrdinalIgnoreCase),
            AgentLabelLabelMatch.Regex => RegexMatches(label),
            _ => string.Equals(label, Value, StringComparison.OrdinalIgnoreCase)
        };
    }

    private bool RegexMatches(string label)
    {
        if (_regex is null)
            return false;

        try
        {
            return _regex.IsMatch(label);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
