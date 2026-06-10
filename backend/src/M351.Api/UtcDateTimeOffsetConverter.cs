using System.Text.Json;
using System.Text.Json.Serialization;

namespace M351.Api;

/// <summary>
/// Serializa DateTimeOffset como ISO-8601 UTC com sufixo "Z" (forma canônica do contrato —
/// Seção 5: "2026-06-09T14:32:07.852Z"), em vez do "+00:00" default do System.Text.Json.
/// </summary>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime); // DateTime Kind=Utc → sufixo "Z"
}
