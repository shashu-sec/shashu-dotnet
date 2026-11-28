using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shashu.Chat;

/// <summary>
/// Reads and writes <see cref="ChatMessage"/>, whose <c>content</c> is either a
/// string or an array of parts. Property order and null handling are those of
/// 1.2.0, so a text-only message serialises to the same bytes.
/// </summary>
internal sealed class ChatMessageJsonConverter : JsonConverter<ChatMessage>
{
    public override ChatMessage Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A chat message must be a JSON object.");

        var comparison = options.PropertyNameCaseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string? role = null, content = null, toolCallId = null, name = null;
        List<ContentPart>? parts = null;
        IReadOnlyList<ToolCall>? toolCalls = null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var property = reader.GetString()!;
            reader.Read();
            if (property.Equals("role", comparison))
            {
                role = reader.GetString();
            }
            else if (property.Equals("content", comparison))
            {
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    content = null;
                    parts = new List<ContentPart>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        using var part = JsonDocument.ParseValue(ref reader);
                        parts.Add(ContentPart.FromWire(part.RootElement));
                    }
                }
                else
                {
                    parts = null;
                    content = reader.GetString();
                }
            }
            else if (property.Equals("tool_calls", comparison))
            {
                toolCalls = JsonSerializer.Deserialize<IReadOnlyList<ToolCall>>(ref reader, options);
            }
            else if (property.Equals("tool_call_id", comparison))
            {
                toolCallId = reader.GetString();
            }
            else if (property.Equals("name", comparison))
            {
                name = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        var message = new ChatMessage(role!, content!)
        {
            ToolCalls = toolCalls,
            ToolCallId = toolCallId,
            Name = name,
        };
        return parts is null ? message : message.WithReadParts(parts.AsReadOnly());
    }

    public override void Write(Utf8JsonWriter writer, ChatMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("role", value.Role);
        if (value.ContentParts is { } parts)
        {
            writer.WriteStartArray("content");
            foreach (var part in parts)
                writer.WriteRawValue(part.ToJson());
            writer.WriteEndArray();
        }
        else if (value.Content is not null
            || options.DefaultIgnoreCondition != JsonIgnoreCondition.WhenWritingNull)
        {
            writer.WriteString("content", value.Content);
        }

        if (value.ToolCalls is not null)
        {
            writer.WritePropertyName("tool_calls");
            JsonSerializer.Serialize(writer, value.ToolCalls, options);
        }

        if (value.ToolCallId is not null)
            writer.WriteString("tool_call_id", value.ToolCallId);
        if (value.Name is not null)
            writer.WriteString("name", value.Name);
        writer.WriteEndObject();
    }
}
