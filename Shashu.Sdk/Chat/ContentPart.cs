using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shashu.Chat;

/// <summary>
/// One part of a multi-part <see cref="ChatMessage"/>: text, an image, or a
/// file, in the OpenAI chat format the gateway inspects and forwards. The
/// Python and Node SDKs send the same shapes.
/// </summary>
/// <remarks>
/// Instances are immutable. <see cref="ToString"/> names the part type only,
/// so attached bytes never reach a log through it.
/// </remarks>
[JsonConverter(typeof(ContentPartJsonConverter))]
public sealed class ContentPart : IEquatable<ContentPart>
{
    // The part exactly as it goes on the wire. Written raw, because the
    // default System.Text.Json encoder escapes '+' and non-ASCII characters
    // and the Python and Node SDKs do not.
    private readonly string _json;

    private ContentPart(string type, string? textValue, string json)
    {
        Type = type;
        TextValue = textValue;
        _json = json;
    }

    /// <summary>The part type, e.g. <c>text</c>, <c>image_url</c> or <c>file</c>.</summary>
    public string Type { get; }

    /// <summary>The text of a <c>text</c> part; null for any other type.</summary>
    public string? TextValue { get; }

    /// <summary>A text part: <c>{"type":"text","text":…}</c>.</summary>
    public static ContentPart Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var json = new StringBuilder(text.Length + 32).Append("{\"type\":\"text\",\"text\":");
        Quote(json, text);
        return new ContentPart("text", text, json.Append('}').ToString());
    }

    /// <summary>
    /// An image part referring to a URL:
    /// <c>{"type":"image_url","image_url":{"url":…}}</c>. The URL is sent as
    /// given. Whether the gateway fetches or inspects a remote URL is decided
    /// by the agent's file policy.
    /// </summary>
    public static ContentPart ImageUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var json = new StringBuilder(url.Length + 64)
            .Append("{\"type\":\"image_url\",\"image_url\":{\"url\":");
        Quote(json, url);
        return new ContentPart("image_url", null, json.Append("}}").ToString());
    }

    /// <summary>An image part carrying the bytes as a base64 <c>data:</c> URI; the SDK does the encoding.</summary>
    public static ContentPart ImageData(byte[] data, string mimeType) => ImageUrl(DataUri(data, mimeType));

    /// <summary>
    /// A file part:
    /// <c>{"type":"file","file":{"filename":…,"file_data":"data:…;base64,…"}}</c>;
    /// the SDK does the base64 encoding. Size and type limits are the gateway's.
    /// </summary>
    public static ContentPart File(byte[] data, string filename, string mimeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        var uri = DataUri(data, mimeType);
        var json = new StringBuilder(uri.Length + filename.Length + 64)
            .Append("{\"type\":\"file\",\"file\":{\"filename\":");
        Quote(json, filename);
        json.Append(",\"file_data\":");
        Quote(json, uri);
        return new ContentPart("file", null, json.Append("}}").ToString());
    }

    /// <summary>The part as its wire JSON. Use it to read a part type this SDK has no accessor for.</summary>
    public string ToJson() => _json;

    /// <inheritdoc />
    public bool Equals(ContentPart? other) => other is not null && _json == other._json;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ContentPart);

    /// <inheritdoc />
    public override int GetHashCode() => _json.GetHashCode(StringComparison.Ordinal);

    /// <summary>The part type only; never the content.</summary>
    public override string ToString() => $"ContentPart {{ Type = {Type} }}";

    // A part read from a response; unknown types and fields are kept as sent.
    internal static ContentPart FromWire(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("A content part must be a JSON object.");
        var type = StringMember(element, "type") ?? "";
        var text = type == "text" ? StringMember(element, "text") : null;
        var json = new StringBuilder();
        Write(json, element);
        return new ContentPart(type, text, json.ToString());
    }

    private static string? StringMember(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string DataUri(byte[] data, string mimeType)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        return $"data:{mimeType};base64,{Convert.ToBase64String(data)}";
    }

    private static void Write(StringBuilder json, JsonElement element)
    {
        var separator = "";
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                json.Append('{');
                foreach (var member in element.EnumerateObject())
                {
                    json.Append(separator);
                    Quote(json, member.Name);
                    json.Append(':');
                    Write(json, member.Value);
                    separator = ",";
                }
                json.Append('}');
                break;
            case JsonValueKind.Array:
                json.Append('[');
                foreach (var item in element.EnumerateArray())
                {
                    json.Append(separator);
                    Write(json, item);
                    separator = ",";
                }
                json.Append(']');
                break;
            case JsonValueKind.String:
                Quote(json, element.GetString()!);
                break;
            default:
                json.Append(element.GetRawText());
                break;
        }
    }

    // The escaping of JavaScript's JSON.stringify.
    private static void Quote(StringBuilder json, string text)
    {
        json.Append('"');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        json.Append(c).Append(text[++i]);
                    else if (c < ' ' || char.IsSurrogate(c))
                        json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        json.Append(c);
                    break;
            }
        }
        json.Append('"');
    }
}

internal sealed class ContentPartJsonConverter : JsonConverter<ContentPart>
{
    public override ContentPart Read(
        ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ContentPart.FromWire(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, ContentPart value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.ToJson());
}
