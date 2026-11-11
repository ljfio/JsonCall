using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace JsonCall.OpenRpc;

/// <summary>
/// Maps .NET types to JSON Schema Draft 07. Object types are hoisted into <see cref="Components"/>
/// and referenced with <c>$ref</c>, which also makes recursive types terminate.
/// </summary>
public sealed class SchemaGenerator
{
    public const string Draft07 = "http://json-schema.org/draft-07/schema#";

    private static readonly NullabilityInfoContext Nullability = new();

    private readonly JsonSerializerOptions _options;
    private readonly Dictionary<Type, string> _names = new();
    private readonly Dictionary<string, Type> _owners = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, JsonObject> _components = new(StringComparer.Ordinal);

    public SchemaGenerator(JsonSerializerOptions options) => _options = options;

    public bool HasComponents => _components.Count > 0;

    /// <summary>The hoisted schemas, ordered by name so output is stable.</summary>
    public JsonObject Components()
    {
        var result = new JsonObject();

        foreach (var (name, schema) in _components)
            result[name] = schema.DeepClone();

        return result;
    }

    public JsonNode Schema(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return MakeNullable(Schema(underlying));

        if (Primitive(type) is { } primitive)
            return primitive;

        if (type.IsEnum)
        {
            var names = new JsonArray();

            foreach (var name in Enum.GetNames(type))
                names.Add(name);

            return new JsonObject { ["type"] = "string", ["enum"] = names };
        }

        if (type == typeof(object) || type == typeof(JsonElement) || type == typeof(JsonNode)
            || type == typeof(JsonDocument) || type == typeof(JsonObject) || type == typeof(JsonArray))
        {
            return new JsonObject();
        }

        if (DictionaryValueType(type) is { } valueType)
            return new JsonObject { ["type"] = "object", ["additionalProperties"] = Schema(valueType) };

        if (ElementType(type) is { } elementType)
            return new JsonObject { ["type"] = "array", ["items"] = Schema(elementType) };

        return Reference(type);
    }

    private static JsonObject? Primitive(Type type)
    {
        if (type == typeof(string) || type == typeof(char) || type == typeof(TimeSpan))
            return Typed("string");

        if (type == typeof(bool))
            return Typed("boolean");

        if (type == typeof(int) || type == typeof(short) || type == typeof(sbyte) || type == typeof(byte) || type == typeof(ushort))
            return Typed("integer", "int32");

        if (type == typeof(long) || type == typeof(uint))
            return Typed("integer", "int64");

        if (type == typeof(ulong))
            return Typed("integer");

        if (type == typeof(float))
            return Typed("number", "float");

        if (type == typeof(double))
            return Typed("number", "double");

        if (type == typeof(decimal))
            return Typed("number");

        if (type == typeof(Guid))
            return Typed("string", "uuid");

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return Typed("string", "date-time");

        if (type == typeof(DateOnly))
            return Typed("string", "date");

        if (type == typeof(TimeOnly))
            return Typed("string", "time");

        if (type == typeof(Uri))
            return Typed("string", "uri");

        if (type == typeof(byte[]))
            return new JsonObject { ["type"] = "string", ["contentEncoding"] = "base64" };

        return null;
    }

    private static JsonObject Typed(string type, string? format = null)
    {
        var schema = new JsonObject { ["type"] = type };

        if (format is not null)
            schema["format"] = format;

        return schema;
    }

    private static JsonNode MakeNullable(JsonNode schema)
    {
        if (schema is not JsonObject obj)
            return schema;

        if (obj.ContainsKey("$ref"))
        {
            return new JsonObject
            {
                ["oneOf"] = new JsonArray(obj, new JsonObject { ["type"] = "null" })
            };
        }

        if (obj["type"] is JsonValue value && value.TryGetValue<string>(out var type))
        {
            var result = new JsonObject { ["type"] = new JsonArray(type, "null") };

            if (obj["enum"] is JsonArray values)
            {
                var copy = (JsonArray)values.DeepClone();
                copy.Add(null);
                result["enum"] = copy;
            }

            if (obj["contentEncoding"] is { } encoding)
                result["contentEncoding"] = encoding.DeepClone();

            return result;
        }

        return schema;
    }

    private static Type? DictionaryValueType(Type type)
    {
        foreach (var candidate in new[] { type }.Concat(type.GetInterfaces()))
        {
            if (!candidate.IsGenericType)
                continue;

            var definition = candidate.GetGenericTypeDefinition();

            if ((definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
                && candidate.GetGenericArguments()[0] == typeof(string))
            {
                return candidate.GetGenericArguments()[1];
            }
        }

        return null;
    }

    private static Type? ElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        foreach (var candidate in new[] { type }.Concat(type.GetInterfaces()))
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return candidate.GetGenericArguments()[0];
        }

        return null;
    }

    private JsonNode Reference(Type type)
    {
        if (!_names.TryGetValue(type, out var name))
        {
            name = Unique(type);
            _names[type] = name;
            _owners[name] = type;

            // Register before visiting properties so a recursive type finds itself.
            var schema = new JsonObject();
            _components[name] = schema;
            Describe(type, schema);
        }

        return new JsonObject { ["$ref"] = "#/components/schemas/" + name };
    }

    private string Unique(Type type)
    {
        var name = Readable(type, fullName: false);

        if (!_owners.ContainsKey(name))
            return name;

        name = Readable(type, fullName: true);

        for (var suffix = 2; _owners.ContainsKey(name); suffix++)
            name = Readable(type, fullName: true) + suffix;

        return name;
    }

    private static string Readable(Type type, bool fullName)
    {
        var baseName = fullName ? (type.FullName ?? type.Name).Replace('+', '_').Replace('.', '_') : type.Name;

        if (!type.IsGenericType)
            return baseName;

        var tick = baseName.IndexOf('`');

        if (tick >= 0)
            baseName = baseName[..tick];

        return baseName + "_" + string.Join("_", type.GetGenericArguments().Select(a => Readable(a, false)));
    }

    private void Describe(Type type, JsonObject schema)
    {
        schema["$schema"] = Draft07;
        schema["type"] = "object";

        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0
                || property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
            {
                continue;
            }

            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                       ?? _options.PropertyNamingPolicy?.ConvertName(property.Name)
                       ?? property.Name;

            properties[name] = Schema(property.PropertyType);

            var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null
                           || (!property.PropertyType.IsValueType
                               && Nullability.Create(property).ReadState == NullabilityState.Nullable);

            if (!nullable)
                required.Add(name);
        }

        schema["properties"] = properties;

        if (required.Count > 0)
            schema["required"] = required;
    }
}
