using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using JsonCall.Core;
using JsonCall.Core.Model;

namespace JsonCall.OpenRpc;

/// <summary>Builds an OpenRPC document from JSON-RPC contract interfaces.</summary>
public sealed class OpenRpcDocumentGenerator
{
    /// <summary>
    /// The OpenRPC version written to documents. It is the newest version the published
    /// meta-schema (open-rpc/meta-schema 1.14.9) accepts.
    /// </summary>
    public const string SupportedVersion = "1.3.2";

    public const string StreamExtension = "x-jsoncall-stream";

    private static readonly NullabilityInfoContext Nullability = new();

    private static readonly JsonSerializerOptions Output = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly OpenRpcOptions _options;

    public OpenRpcDocumentGenerator(OpenRpcOptions? options = null, JsonSerializerOptions? serializerOptions = null)
    {
        _options = options ?? new OpenRpcOptions();
        SerializerOptions = _options.SerializerOptions ?? serializerOptions ?? JsonRpcSerializer.DefaultOptions;
    }

    public JsonSerializerOptions SerializerOptions { get; }

    public JsonObject Generate(params Type[] contracts) => Generate((IEnumerable<Type>)contracts);

    public JsonObject Generate(IEnumerable<Type> contracts)
    {
        var contractList = contracts.ToList();
        var schemas = new SchemaGenerator(SerializerOptions);
        var methods = new JsonArray();
        var seen = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (var contract in contractList)
        {
            var contractTags = contract.GetCustomAttributes<JsonRpcTagAttribute>().Select(t => t.Name).ToList();
            var style = ContractReflection.GetParamStyle(contract);

            foreach (var method in ContractReflection.GetMethods(contract))
            {
                if (seen.TryGetValue(method.Name, out var other))
                {
                    throw new InvalidOperationException(
                        $"The JSON-RPC method '{method.Name}' is declared by both {other.FullName} and {contract.FullName}.");
                }

                seen[method.Name] = contract;
                methods.Add(DescribeMethod(method, style, contractTags, schemas));
            }
        }

        var document = new JsonObject
        {
            ["openrpc"] = SupportedVersion,
            ["info"] = Info(contractList)
        };

        if (_options.PublicBaseUrl is { } url)
        {
            document["servers"] = new JsonArray(new JsonObject { ["name"] = "default", ["url"] = url });
        }

        document["methods"] = methods;

        if (schemas.HasComponents)
            document["components"] = new JsonObject { ["schemas"] = schemas.Components() };

        return document;
    }

    /// <summary>Serializes a document with stable formatting (two-space indent, LF line endings, no BOM).</summary>
    public static string ToJson(JsonNode document) => document.ToJsonString(Output);

    public string GenerateJson(params Type[] contracts) => ToJson(Generate(contracts));

    private JsonObject Info(List<Type> contracts)
    {
        var attribute = contracts.Select(c => c.GetCustomAttribute<JsonRpcContractAttribute>()).FirstOrDefault(a => a is not null);

        var info = new JsonObject
        {
            ["title"] = _options.Title ?? attribute?.Title ?? contracts.FirstOrDefault()?.Assembly.GetName().Name ?? "JSON-RPC API",
            ["version"] = _options.Version ?? attribute?.Version ?? "1.0.0"
        };

        if (_options.Description is { } description)
            info["description"] = description;

        return info;
    }

    private JsonObject DescribeMethod(ContractMethod method, JsonRpcParamStyle style, List<string> contractTags,
        SchemaGenerator schemas)
    {
        var docs = XmlDocumentation.ForMethod(method.Method);
        var result = new JsonObject { ["name"] = method.Name };

        if (docs?.Summary is { } summary)
            result["summary"] = summary;

        var description = new List<string>();

        if (docs?.Remarks is { } remarks)
            description.Add(remarks);

        var obsolete = method.Method.GetCustomAttribute<ObsoleteAttribute>();

        if (obsolete is not null && !string.IsNullOrWhiteSpace(obsolete.Message))
            description.Add("Obsolete: " + obsolete.Message);

        if (description.Count > 0)
            result["description"] = string.Join("\n\n", description);

        var tags = contractTags.Concat(method.Method.GetCustomAttributes<JsonRpcTagAttribute>().Select(t => t.Name))
            .Distinct(StringComparer.Ordinal).ToList();

        if (tags.Count > 0)
            result["tags"] = new JsonArray(tags.Select(t => (JsonNode)new JsonObject { ["name"] = t }).ToArray());

        result["paramStructure"] = style switch
        {
            JsonRpcParamStyle.ByName => "by-name",
            JsonRpcParamStyle.Either => "either",
            _ => "by-position"
        };

        result["params"] = Params(method, docs, schemas);

        if (method.Kind != ResultKind.Void && !method.IsNotification)
        {
            result["result"] = new JsonObject
            {
                ["name"] = "result",
                ["schema"] = schemas.Schema(method.ResultType ?? typeof(object))
            };
        }

        if (Errors(method) is { Count: > 0 } errors)
            result["errors"] = errors;

        if (obsolete is not null)
            result["deprecated"] = true;

        if (method.Kind == ResultKind.Stream)
            result[StreamExtension] = true;

        return result;
    }

    private static JsonArray Params(ContractMethod method, XmlDocumentation.MethodDocs? docs, SchemaGenerator schemas)
    {
        var parameters = method.Parameters;
        var result = new JsonArray();

        // A parameter is optional when it has a default, or is nullable and nothing required follows it.
        // That keeps required parameters first without ever changing wire positions.
        var optional = new bool[parameters.Length];
        var tailOptional = true;

        for (var i = parameters.Length - 1; i >= 0; i--)
        {
            var parameter = parameters[i];
            var isNullable = Nullable.GetUnderlyingType(parameter.ParameterType) is not null
                             || (!parameter.ParameterType.IsValueType
                                 && Nullability.Create(parameter).ReadState == NullabilityState.Nullable);

            optional[i] = tailOptional && (parameter.HasDefaultValue || isNullable);
            tailOptional = optional[i];
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var schema = schemas.Schema(parameter.ParameterType);

            if (parameter.HasDefaultValue && parameter.DefaultValue is { } value && schema is JsonObject obj)
                obj["default"] = DefaultValue(value);

            var entry = new JsonObject
            {
                ["name"] = parameter.Name,
                ["required"] = !optional[i],
                ["schema"] = schema
            };

            if (docs is not null && parameter.Name is { } name && docs.Params.TryGetValue(name, out var text))
                entry["description"] = text;

            result.Add(entry);
        }

        return result;
    }

    private static JsonNode? DefaultValue(object value) => value switch
    {
        string s => s,
        bool b => b,
        int n => n,
        long n => n,
        double d => d,
        float f => f,
        decimal m => m,
        Enum e => e.ToString(),
        _ => JsonValue.Create(value.ToString())
    };

    private static JsonArray Errors(ContractMethod method)
    {
        var result = new JsonArray();
        var codes = new HashSet<int>();

        foreach (var error in method.Method.GetCustomAttributes<JsonRpcErrorAttribute>())
        {
            if (JsonRpcErrorCodes.IsReserved(error.Code))
            {
                throw new InvalidOperationException(
                    $"The method '{method.Name}' declares the error code {error.Code}, which is in the range -32768 to -32000 reserved by JSON-RPC.");
            }

            if (!codes.Add(error.Code))
            {
                throw new InvalidOperationException(
                    $"The method '{method.Name}' declares the error code {error.Code} more than once.");
            }

            result.Add(new JsonObject { ["code"] = error.Code, ["message"] = error.Message });
        }

        return result;
    }
}
