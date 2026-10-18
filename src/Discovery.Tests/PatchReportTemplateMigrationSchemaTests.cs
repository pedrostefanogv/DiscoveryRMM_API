using System.Reflection;
using System.Text.Json;
using Discovery.Migrations.Migrations;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Valida os JSONs (layout/execution schema) gerados pelos templates embutidos
/// de patches em M232. Como o FluentMigrator só é exercitado contra banco, um
/// schema malformado passaria pelo build e só quebraria no console — este teste
/// pega isso em CI.
/// </summary>
[TestFixture]
public class PatchReportTemplateMigrationSchemaTests
{
    private static string ReadStaticString(string propertyName)
    {
        var property = typeof(M232_SeedPatchReportTemplates).GetProperty(
            propertyName,
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
        Assert.That(property, Is.Not.Null, $"propriedade {propertyName} não encontrada");
        return (string)property!.GetValue(null)!;
    }

    [TestCase("WindowsUpdatesSchema")]
    [TestCase("PatchComplianceSchema")]
    [TestCase("PatchApprovalsSchema")]
    [TestCase("PatchApprovalAuditSchema")]
    [TestCase("AgentsSchema")]
    public void ExecutionSchema_IsValidJsonWithFiltersAndPresets(string propertyName)
    {
        var json = ReadStaticString(propertyName);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            Assert.Fail($"{propertyName} não é JSON válido: {ex.Message}");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            Assert.That(root.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(root.TryGetProperty("allowedOrientations", out var orientations), Is.True);
            Assert.That(orientations.ValueKind, Is.EqualTo(JsonValueKind.Array));
            Assert.That(root.TryGetProperty("filters", out var filters), Is.True);
            Assert.That(filters.ValueKind, Is.EqualTo(JsonValueKind.Array));
            Assert.That(filters.GetArrayLength(), Is.GreaterThan(0));
            Assert.That(root.TryGetProperty("sampleFilterPresets", out var presets), Is.True);
            Assert.That(presets.ValueKind, Is.EqualTo(JsonValueKind.Array));
            Assert.That(presets.GetArrayLength(), Is.GreaterThan(0));

            // Todo preset precisa ter filtersJson parseável.
            foreach (var preset in presets.EnumerateArray())
            {
                Assert.That(preset.TryGetProperty("filtersJson", out var filtersJson), Is.True);
                var raw = filtersJson.GetString();
                Assert.That(raw, Is.Not.Null.And.Not.Empty);
                using var parsed = JsonDocument.Parse(raw!);
                Assert.That(parsed.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object));
            }

            // Cada filtro precisa de name/label/type/uiComponent.
            foreach (var filter in filters.EnumerateArray())
            {
                foreach (var required in new[] { "name", "label", "type", "uiComponent" })
                    Assert.That(filter.TryGetProperty(required, out _), Is.True,
                        $"{propertyName}: filtro sem '{required}'");
            }
        }
    }
}
