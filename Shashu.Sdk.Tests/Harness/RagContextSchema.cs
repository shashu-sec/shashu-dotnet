using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Shashu.Tests.Harness;

/// <summary>
/// Validates a payload against <c>contracts/rag_context.v1.schema.json</c> —
/// the real file in this repo, read from disk, not a vendored copy or a
/// restatement of its rules.
/// </summary>
/// <remarks>
/// <para>The .NET SDK lives in the gateway repo, so it reads the contract
/// directly. That is the point: a schema change lands here without anyone
/// remembering to re-vendor it.</para>
/// <para>This is a deliberately small validator covering exactly the keywords
/// <c>rag_context.v1</c> uses. It is not a general JSON Schema implementation
/// and must not grow into one — the reason it can be trusted is
/// <c>RagContextSchemaTests</c>, which runs it over every fixture in
/// <c>contracts/fixtures/rag_context/</c> and requires it to accept all of
/// <c>valid/</c> and reject all of <c>invalid/</c>. A validator that quietly
/// ignored a keyword would fail there.</para>
/// </remarks>
internal static class RagContextSchema
{
    private static readonly Lazy<JsonObject> Schema = new(Load);
    private static readonly Lazy<DirectoryInfo> Contracts = new(FindContracts);

    /// <summary>The parsed contract.</summary>
    internal static JsonObject Document => Schema.Value;

    /// <summary>The <c>contracts/</c> directory in this repo.</summary>
    internal static DirectoryInfo Directory => Contracts.Value;

    /// <summary>Every validation failure, empty when the payload conforms.</summary>
    internal static IReadOnlyList<string> Validate(JsonNode? payload)
    {
        var errors = new List<string>();
        Check(payload, Schema.Value, "$", errors);
        return errors;
    }

    /// <summary>True when the payload conforms.</summary>
    internal static bool IsValid(JsonNode? payload) => Validate(payload).Count == 0;

    private static JsonObject Load()
    {
        var path = Path.Combine(Contracts.Value.FullName, "rag_context.v1.schema.json");
        return (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
    }

    /// <summary>
    /// Walk up from the test assembly to the repo root. CI runs
    /// <c>dotnet test</c> from <c>sdks/dotnet</c>, and the binaries sit several
    /// levels below that, so the directory is found rather than assumed.
    /// </summary>
    private static DirectoryInfo FindContracts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = new DirectoryInfo(Path.Combine(dir.FullName, "contracts"));
            if (candidate.Exists && File.Exists(Path.Combine(candidate.FullName, "rag_context.v1.schema.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "contracts/rag_context.v1.schema.json not found walking up from "
            + AppContext.BaseDirectory
            + ". The .NET SDK reads the contract directly rather than vendoring it.");
    }

    private static void Check(JsonNode? node, JsonObject schema, string path, List<string> errors)
    {
        if (schema["const"] is { } konst && !JsonNode.DeepEquals(node, konst))
            errors.Add($"{path}: expected const {konst.ToJsonString()}");

        if (schema["enum"] is JsonArray choices
            && !choices.Any(c => JsonNode.DeepEquals(node, c)))
            errors.Add($"{path}: {node?.ToJsonString()} is not one of the enum values");

        if (schema["type"]?.GetValue<string>() is { } type && !TypeMatches(node, type))
        {
            errors.Add($"{path}: expected type {type}");
            return;   // every other keyword assumes the type held
        }

        switch (node)
        {
            case JsonObject obj: CheckObject(obj, schema, path, errors); break;
            case JsonArray arr: CheckArray(arr, schema, path, errors); break;
            case JsonValue when node.GetValueKind() == JsonValueKind.String:
                CheckString(node.GetValue<string>(), schema, path, errors);
                break;
            case JsonValue when node.GetValueKind() == JsonValueKind.Number:
                CheckNumber(node.GetValue<double>(), schema, path, errors);
                break;
        }
    }

    private static void CheckObject(JsonObject obj, JsonObject schema, string path, List<string> errors)
    {
        var properties = schema["properties"] as JsonObject;

        if (schema["required"] is JsonArray required)
        {
            foreach (var name in required.Select(r => r!.GetValue<string>()))
            {
                if (!obj.ContainsKey(name)) errors.Add($"{path}: missing required property '{name}'");
            }
        }

        if (schema["additionalProperties"]?.GetValueKind() == JsonValueKind.False)
        {
            foreach (var (key, _) in obj)
            {
                if (properties?[key] is null) errors.Add($"{path}: additional property '{key}' is not allowed");
            }
        }

        if (properties is null) return;
        foreach (var (key, value) in obj)
        {
            if (properties[key] is JsonObject sub) Check(value, sub, $"{path}.{key}", errors);
        }
    }

    private static void CheckArray(JsonArray arr, JsonObject schema, string path, List<string> errors)
    {
        if (schema["minItems"]?.GetValue<int>() is { } min && arr.Count < min)
            errors.Add($"{path}: {arr.Count} items, minItems is {min}");
        if (schema["maxItems"]?.GetValue<int>() is { } max && arr.Count > max)
            errors.Add($"{path}: {arr.Count} items, maxItems is {max}");

        if (schema["items"] is not JsonObject items) return;
        for (var i = 0; i < arr.Count; i++) Check(arr[i], items, $"{path}[{i}]", errors);
    }

    private static void CheckString(string value, JsonObject schema, string path, List<string> errors)
    {
        if (schema["minLength"]?.GetValue<int>() is { } min && value.Length < min)
            errors.Add($"{path}: length {value.Length}, minLength is {min}");
        if (schema["maxLength"]?.GetValue<int>() is { } max && value.Length > max)
            errors.Add($"{path}: length {value.Length}, maxLength is {max}");
        if (schema["pattern"]?.GetValue<string>() is { } pattern && !Regex.IsMatch(value, pattern))
            errors.Add($"{path}: does not match pattern {pattern}");
    }

    private static void CheckNumber(double value, JsonObject schema, string path, List<string> errors)
    {
        if (schema["minimum"]?.GetValue<double>() is { } min && value < min)
            errors.Add($"{path}: {value} is below minimum {min}");
        if (schema["maximum"]?.GetValue<double>() is { } max && value > max)
            errors.Add($"{path}: {value} is above maximum {max}");
    }

    private static bool TypeMatches(JsonNode? node, string type) => type switch
    {
        "object" => node is JsonObject,
        "array" => node is JsonArray,
        "string" => node?.GetValueKind() == JsonValueKind.String,
        // JSON has no integer type; the contract means a number with no
        // fractional part, which is how every JSON Schema validator reads it.
        "integer" => node?.GetValueKind() == JsonValueKind.Number
                     && Math.Abs(node.GetValue<double>() % 1) < double.Epsilon,
        "number" => node?.GetValueKind() == JsonValueKind.Number,
        "boolean" => node?.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "null" => node is null || node.GetValueKind() == JsonValueKind.Null,
        _ => true,
    };
}
