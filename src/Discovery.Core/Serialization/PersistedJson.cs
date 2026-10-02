using System.Text.Json;

namespace Discovery.Core.Serialization;

/// <summary>
/// Opções de serialização para JSON **persistido em coluna** e consumido pela UI
/// (ex.: `processing_scope_state.last_result_json`).
///
/// Convenção: grava em camelCase (Web defaults) e lê de forma case-insensitive,
/// então dados antigos gravados em PascalCase — default do System.Text.Json —
/// continuam válidos. Use sempre estas opções em vez de `new JsonSerializerOptions()`
/// para não repetir o bug de casing entre produtor (PascalCase) e consumidor
/// (camelCase).
/// </summary>
public static class PersistedJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
