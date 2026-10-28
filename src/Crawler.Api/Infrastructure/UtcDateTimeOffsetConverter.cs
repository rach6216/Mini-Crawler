using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crawler.Api.Infrastructure;

/// <summary>Writes timestamps as UTC ISO-8601 with a <c>Z</c> suffix, as the API contract specifies.</summary>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime);
}
