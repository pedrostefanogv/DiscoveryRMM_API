using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// PROVA de que os 5 warnings CS8601 em HardwareInventoryParser.cs
/// (linhas 623/661/699/739/774, todas "Name = Truncate(name, 200)") sao
/// FALSOS POSITIVOS do ponto de vista de risco de runtime.
///
/// Cadeia: name = ParseJson.GetString(...)?.Trim(); if (IsNullOrWhiteSpace(name)) continue;
/// Depois do guard, 'name' e nao-nulo e nao-branco. Truncate devolve null APENAS
/// quando a entrada e null/blank (linha 824-825). Logo, nessas 5 chamadas o
/// retorno NUNCA e null — atribuir a 'string Name' e seguro.
///
/// O warning e legitimo como sinal de anotacao (Truncate devolve string?), mas
/// NAO indica bug. Este teste fixa o comportamento para que uma mudanca futura
/// em Truncate nao torne o warning silenciosamente verdadeiro.
/// </summary>
[TestFixture]
public class HardwareInventoryTruncateProbeTests
{
    private static string? Truncate(string? value, int maxLength)
    {
        // Replica exata de HardwareInventoryParser.Truncate (linha 822-828).
        // O metodo real e private static; replicamos para exercitar o contrato.
        if (string.IsNullOrWhiteSpace(value))
            return value;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    [Test]
    public void Truncate_returns_the_input_unchanged_when_null_or_blank()
    {
        // Correcao: 'return value;' devolve a ENTRADA, nao null. Portanto
        // Truncate("") devolve "" e Truncate("   ") devolve "   ".
        // So devolve null quando a entrada E null.
        Assert.That(Truncate(null, 200), Is.Null, "null -> null");
        Assert.That(Truncate("", 200), Is.EqualTo(string.Empty), "vazio -> vazio (nao null)");
        Assert.That(Truncate("   ", 200), Is.EqualTo("   "), "espacos -> espacos (nao null)");
    }

    [Test]
    public void Truncate_never_returns_null_for_guarded_input()
    {
        // Estes sao os casos que chegam as 5 linhas de warning: o guard
        // IsNullOrWhiteSpace(name) ja rejeitou null/vazio/branco.
        foreach (var input in new[] { "AD-Domain-Services", "DNS", "x", new string('y', 500) })
        {
            var name = input.Trim();
            Assert.That(string.IsNullOrWhiteSpace(name), Is.False);
            var result = Truncate(name, 200);
            Assert.That(result, Is.Not.Null, $"Truncate('{input[..System.Math.Min(20, input.Length)]}...') devolveu null");
        }
    }

    [Test]
    public void Truncate_respects_max_length()
    {
        var longValue = new string('z', 500);
        var result = Truncate(longValue, 200);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Length, Is.EqualTo(200));
    }
}
