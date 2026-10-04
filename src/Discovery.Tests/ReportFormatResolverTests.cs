using Discovery.Core.Enums;
using Discovery.Core.Helpers;

namespace Discovery.Tests;

/// <summary>
/// A UI envia formatos em varios formatos (numero, nome, camelCase) e o extinto
/// Pdf = 1 nao pode bloquear a geracao. O resolvedor e compartilhado por
/// preview, execucao e agendamento.
/// </summary>
public class ReportFormatResolverTests
{
    [TestCase("Markdown", ReportFormat.Markdown)]
    [TestCase("markdown", ReportFormat.Markdown)]
    [TestCase("md", ReportFormat.Markdown)]
    [TestCase("3", ReportFormat.Markdown)]
    [TestCase(3, ReportFormat.Markdown)]
    [TestCase("Xlsx", ReportFormat.Xlsx)]
    [TestCase("0", ReportFormat.Xlsx)]
    [TestCase("Csv", ReportFormat.Csv)]
    [TestCase("2", ReportFormat.Csv)]
    public void Resolve_AcceptsFlexibleValues(object value, ReportFormat expected)
        => Assert.That(ReportFormatResolver.Resolve(value), Is.EqualTo(expected));

    [TestCase("1")]
    [TestCase(1)]
    [TestCase("Pdf")]
    [TestCase("999")]
    public void Resolve_DoesNotReturnUndefinedFormats(object value)
        => Assert.That(ReportFormatResolver.Resolve(value), Is.Null);

    [TestCase("1")]
    [TestCase(1)]
    [TestCase("Pdf")]
    public void IsLegacyPdf_DetectsRemovedPdf(object value)
        => Assert.That(ReportFormatResolver.IsLegacyPdf(value), Is.True);
}
