using System.Text.Json;
using System.Text.Json.Serialization;

namespace Discovery.Core.Serialization;

/// <summary>
/// Lê <see cref="Guid"/> e <see cref="Guid?"/> tolerando valores que o agent
/// envia como string VAZIA ou em branco.
/// </summary>
/// <remarks>
/// BUG CORRIGIDO (mensagens do usuário final não chegavam ao console do
/// suporte): o envelope do chat de suporte é serializado pelo agent em Go com
/// tags SEM `omitempty`, então uma conversa AVULSA (sem chamado) publicava
/// <c>"ticketId":""</c> e <c>"agentId":""</c>. O contrato do servidor
/// (<c>TicketChatEnvelope.TicketId</c> é <c>Guid?</c> e <c>AgentId</c> é
/// <c>Guid</c>) não aceita string vazia e o System.Text.Json lançava
/// <c>JsonException</c> ("The JSON value could not be converted to
/// System.Nullable`1[System.Guid]. Path: $.ticketId"). O envelope era
/// DESCARTADO e a mensagem nunca era persistida — o operador não via nada no
/// navegador, embora o agent tivesse recebido PubAck do JetStream.
///
/// O agent foi corrigido para OMITIR os campos vazios, mas este conversor é a
/// defesa do SERVIDOR: toda máquina já instalada continua publicando o formato
/// antigo até receber o self-update, e um único envelope malformado não pode
/// derrubar a ingestão inteira.
///
/// Semântica: vazio/em branco → <c>null</c> para <c>Guid?</c> e
/// <c>Guid.Empty</c> para <c>Guid</c> (o AgentId é sobrescrito pelo id do
/// subject na ingestão, então vazio não é um valor útil).
/// </remarks>
public sealed class LenientGuidJsonConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert == typeof(Guid) || typeToConvert == typeof(Guid?);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => typeToConvert == typeof(Guid?)
            ? new NullableGuidConverter()
            : new GuidConverter();

    private sealed class GuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return Guid.Empty;

            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Valor de Guid deve ser uma string.");

            var raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return Guid.Empty;

            return Guid.TryParse(raw, out var parsed)
                ? parsed
                : throw new JsonException($"O valor JSON não está em um formato de Guid suportado: '{raw}'.");
        }

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }

    private sealed class NullableGuidConverter : JsonConverter<Guid?>
    {
        public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Valor de Guid deve ser uma string.");

            var raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return Guid.TryParse(raw, out var parsed)
                ? parsed
                : throw new JsonException($"O valor JSON não está em um formato de Guid suportado: '{raw}'.");
        }

        public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteStringValue(value.Value);
            else
                writer.WriteNullValue();
        }
    }
}
