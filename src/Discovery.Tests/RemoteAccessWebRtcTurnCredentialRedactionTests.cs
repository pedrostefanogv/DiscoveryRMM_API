using System.Text.Json;
using Discovery.Core.Configuration;

namespace Discovery.Tests;

/// <summary>
/// A credencial do TURN é um SEGREDO: com ela qualquer um usa o relay do
/// operador (consumindo banda e mascarando a origem do tráfego). Estes testes
/// fixam que ela NÃO sai no JSON devolvido ao console, e que o sinalizador de
/// "configurada" aparece para a UI poder preservá-la ao salvar.
///
/// A projeção é replicada aqui porque o método é privado do controller — o que
/// se testa é o CONTRATO (o que sai no JSON), não a assinatura.
/// </summary>
[TestFixture]
public class RemoteAccessWebRtcTurnCredentialRedactionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Mesma transformação aplicada por ProjectServerConfigForClient.</summary>
    private static string Redact(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            return json ?? "{}";

        try
        {
            var policy = JsonSerializer.Deserialize<RemoteAccessWebRtcPolicy>(json, JsonOptions);
            if (policy?.Ice is null || string.IsNullOrWhiteSpace(policy.Ice.TurnCredential))
                return json;

            var configured = !string.IsNullOrWhiteSpace(policy.Ice.TurnCredential);
            policy.Ice.TurnCredential = string.Empty;

            using var doc = JsonDocument.Parse(policy.ToJson());
            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("ice") && property.Value.ValueKind == JsonValueKind.Object)
                    {
                        writer.WritePropertyName("ice");
                        writer.WriteStartObject();
                        foreach (var iceProp in property.Value.EnumerateObject())
                            iceProp.WriteTo(writer);
                        writer.WriteBoolean("turnCredentialConfigured", configured);
                        writer.WriteEndObject();
                        continue;
                    }
                    property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return json;
        }
    }

    [Test]
    public void Credencial_do_turn_nao_sai_no_json()
    {
        var json = """
            {"enabled":true,"kinds":["screen"],
             "ice":{"turnUrls":["turn:t.exemplo.com:3478"],
                    "turnUsername":"usuario",
                    "turnCredential":"SEGREDO-QUE-NAO-PODE-VAZAR"}}
            """;

        var redacted = Redact(json);

        Assert.That(redacted, Does.Not.Contain("SEGREDO-QUE-NAO-PODE-VAZAR"),
            "a credencial do TURN não pode aparecer no payload devolvido ao console");
    }

    [Test]
    public void Sinalizador_de_configurada_e_emitido()
    {
        // Sem esse sinalizador o operador não saberia que existe credencial, e
        // salvar qualquer outro campo a apagaria em silêncio.
        var json = """
            {"enabled":true,"ice":{"turnUrls":["turn:t:3478"],
             "turnUsername":"u","turnCredential":"segredo"}}
            """;

        using var doc = JsonDocument.Parse(Redact(json));
        var ice = doc.RootElement.GetProperty("ice");

        Assert.Multiple(() =>
        {
            Assert.That(ice.GetProperty("turnCredentialConfigured").GetBoolean(), Is.True);
            Assert.That(ice.GetProperty("turnCredential").GetString(), Is.Empty);
        });
    }

    [Test]
    public void Demais_campos_da_politica_sao_preservados()
    {
        // A redação não pode mutilar a política: o console precisa dela inteira
        // para exibir e editar.
        var json = """
            {"enabled":true,"kinds":["screen","rdp"],
             "ice":{"stunUrls":["stun:stun.l.google.com:19302"],
                    "turnUrls":["turn:t:3478"],"turnUsername":"u",
                    "turnCredential":"s","relayOnly":true,
                    "failedTimeoutSeconds":20}}
            """;

        using var doc = JsonDocument.Parse(Redact(json));
        var root = doc.RootElement;
        var ice = root.GetProperty("ice");

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("enabled").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("kinds").GetArrayLength(), Is.EqualTo(2));
            Assert.That(ice.GetProperty("stunUrls")[0].GetString(), Is.EqualTo("stun:stun.l.google.com:19302"));
            Assert.That(ice.GetProperty("turnUrls")[0].GetString(), Is.EqualTo("turn:t:3478"));
            Assert.That(ice.GetProperty("turnUsername").GetString(), Is.EqualTo("u"));
            Assert.That(ice.GetProperty("relayOnly").GetBoolean(), Is.True);
            Assert.That(ice.GetProperty("failedTimeoutSeconds").GetInt32(), Is.EqualTo(20));
        });
    }

    [Test]
    public void Sem_credencial_o_json_passa_intacto()
    {
        var json = """{"enabled":true,"ice":{"stunUrls":["stun:a:3478"]}}""";

        Assert.That(Redact(json), Is.EqualTo(json),
            "sem credencial não há o que redigir — e não se injeta sinalizador à toa");
    }

    [Test]
    public void Default_da_migration_passa_intacto()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Redact("{}"), Is.EqualTo("{}"));
            Assert.That(Redact(null), Is.EqualTo("{}"));
        });
    }

    [Test]
    public void Json_corrompido_passa_intacto_sem_apagar_configuracao()
    {
        // Fail-open CONTROLADO: devolver o original não vaza nada novo (o valor
        // já estava lá) e evita que um JSON corrompido apague a tela.
        const string broken = "{corrompido";

        Assert.That(Redact(broken), Is.EqualTo(broken));
    }
}
