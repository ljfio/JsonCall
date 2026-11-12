using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JsonCall.Generator.Core;

internal sealed partial class ClientGenerator
{
    private sealed class ParamModel
    {
        public string Wire = "";
        public string CsName = "";
        public string Type = "";
        public bool Required;
        public string? Doc;
    }

    private sealed class ErrorModel
    {
        public int Code;
        public string Message = "";
        public string ClassName = "";
    }

    private sealed class MethodModel
    {
        public string Wire = "";
        public string CsName = "";
        public string? Summary;
        public readonly List<ParamModel> Params = new();
        public string? ResultType;
        public bool IsStream;
        public bool IsPush;
        public bool Deprecated;
        public string? Structure;
        public readonly List<ErrorModel> Errors = new();
    }

    private readonly HashSet<string> _methodNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pushNames = new(StringComparer.Ordinal);

    private MethodModel BuildMethod(JNode method, string path)
    {
        var wire = method.GetString("name")!;
        var model = new MethodModel
        {
            Wire = wire,
            Summary = method.GetString("summary") ?? method.GetString("description"),
            Deprecated = method.GetBool("deprecated"),
            Structure = method.GetString("paramStructure"),
            IsStream = method.GetBool("x-jsoncall-stream")
        };

        // A result-less method marked "server-to-client" is pushed by the server: the client subscribes, not calls.
        model.IsPush = method.GetString("x-jsoncall-direction") == "server-to-client"
                       && method.Get("result") is not { IsObject: true };

        var lastSegment = wire.Substring(wire.LastIndexOf('.') + 1);
        var prefix = model.IsPush ? "On" : "";
        var suffix = model.IsPush ? "" : "Async";
        var names = model.IsPush ? _pushNames : _methodNames;
        var name = prefix + Naming.Pascal(lastSegment) + suffix;

        if (!names.Add(name))
        {
            name = prefix + Naming.Pascal(wire) + suffix;

            for (var i = 2; !names.Add(name); i++)
                name = prefix + Naming.Pascal(wire) + i + suffix;
        }

        model.CsName = name;
        var hint = Naming.Pascal(wire);
        var usedNames = new HashSet<string>(StringComparer.Ordinal) { "cancellationToken" };

        if (method.Get("params") is { IsArray: true } parameters)
        {
            for (var j = 0; j < parameters.Items!.Count; j++)
            {
                var descriptor = ResolveDescriptor(parameters.Items[j], path + ".params[" + j + "]", out var descriptorPath);
                var parameterName = descriptor.GetString("name")!;
                var csName = Naming.Camel(parameterName);

                for (var k = 2; !usedNames.Add(csName.TrimStart('@')); k++)
                    csName = Naming.Camel(parameterName) + k;

                var type = descriptor.Get("schema") is { } schema
                    ? Map(schema, path + ".params[" + j + "].schema", hint + Naming.Pascal(parameterName))
                    : "JsonElement";

                model.Params.Add(new ParamModel
                {
                    Wire = parameterName,
                    CsName = csName,
                    Type = type,
                    Required = descriptor.GetBool("required"),
                    Doc = descriptor.GetString("description") ?? descriptor.GetString("summary")
                });
            }
        }

        if (method.Get("result") is { IsObject: true } resultNode)
        {
            var result = ResolveDescriptor(resultNode, path + ".result", out var resultPath);

            model.ResultType = result.Get("schema") is { } resultSchema
                ? Map(resultSchema, path + ".result.schema", hint + "Result")
                : "JsonElement";
        }
        else
        {
            model.IsStream = false;
        }

        if (method.Get("errors") is { IsArray: true } errors)
        {
            foreach (var errorNode in errors.Items!)
            {
                var error = errorNode;

                if (error.GetString("$ref") is { } reference
                    && Lookup(reference, "#/components/errors/", "components", "errors") is { } target)
                {
                    error = target;
                }

                if (error.Get("code") is not { Kind: JKind.Number } code
                    || !int.TryParse(code.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    continue;
                }

                if (model.Errors.Any(e => e.Code == number))
                    continue;

                var sign = number < 0 ? "Minus" : "";

                model.Errors.Add(new ErrorModel
                {
                    Code = number,
                    Message = error.GetString("message") ?? "",
                    ClassName = Unique(hint + "Error" + sign + Math.Abs(number).ToString(CultureInfo.InvariantCulture) + "Exception")
                });
            }
        }

        foreach (var type in model.Params.Select(p => p.Type).Concat(model.ResultType is null ? Array.Empty<string>() : new[] { model.ResultType }))
        {
            if (ContextName(type) is { } contextName)
                _contextTypes.Add(contextName);
        }

        return model;
    }

    private static readonly HashSet<string> ValueTypes = new(StringComparer.Ordinal)
    {
        "int", "long", "bool", "double", "float", "decimal", "Guid", "DateTimeOffset", "DateOnly", "TimeOnly"
    };

    /// <summary>The type as written in typeof(): nullable reference types are not allowed there.</summary>
    private static string? ContextName(string type)
    {
        if (!type.EndsWith("?", StringComparison.Ordinal))
            return type;

        var inner = type.Substring(0, type.Length - 1);
        return ValueTypes.Contains(inner) ? type : inner;
    }

    // ---- emission -----------------------------------------------------

    private static void Doc(StringBuilder sb, string indent, string? summary, IEnumerable<KeyValuePair<string, string?>>? parameters = null)
    {
        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.Append(indent).AppendLine("/// <summary>");

            foreach (var line in summary!.Replace("\r", "").Split('\n'))
                sb.Append(indent).Append("/// ").AppendLine(Naming.XmlEscape(line.Trim()));

            sb.Append(indent).AppendLine("/// </summary>");
        }

        if (parameters is null)
            return;

        foreach (var parameter in parameters)
        {
            if (!string.IsNullOrWhiteSpace(parameter.Value))
            {
                sb.Append(indent).Append("/// <param name=\"").Append(parameter.Key.TrimStart('@')).Append("\">")
                    .Append(Naming.XmlEscape(string.Join(" ", parameter.Value!.Replace("\r", "").Split('\n').Select(l => l.Trim()))))
                    .AppendLine("</param>");
            }
        }
    }

    private string Emit(string clientName, List<MethodModel> methods)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("//     Generated by JsonCall from an OpenRPC document. Changes will be lost.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS1591, CS8618, CS0612, CS0618, CS0419");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using JsonCall.Core;");
        sb.AppendLine("using JsonCall.Core.Model;");
        sb.AppendLine("using JsonCall.Core.Protocol;");
        sb.AppendLine();
        sb.Append("namespace ").Append(_settings.Namespace).AppendLine();
        sb.AppendLine("{");

        foreach (var decl in _decls.Values)
        {
            switch (decl)
            {
                case EnumDecl e: EmitEnum(sb, e); break;
                case RecordDecl r: EmitRecord(sb, r); break;
                case UnionDecl u: EmitUnion(sb, u); break;
            }

            sb.AppendLine();
        }

        foreach (var method in methods)
        {
            foreach (var error in method.Errors)
            {
                sb.Append("    /// <summary>The error ").Append(error.Code.ToString(CultureInfo.InvariantCulture))
                    .Append(" documented for ").Append(Naming.XmlEscape(method.Wire)).AppendLine(".</summary>");
                sb.Append("    public sealed class ").Append(error.ClassName).AppendLine(" : JsonRpcException");
                sb.AppendLine("    {");
                sb.Append("        public ").Append(error.ClassName).AppendLine("(string message, object? data) : base("
                                                                             + error.Code.ToString(CultureInfo.InvariantCulture) + ", message, data)");
                sb.AppendLine("        {");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                sb.AppendLine();
            }
        }

        EmitInterface(sb, clientName, methods);
        sb.AppendLine();
        EmitClient(sb, clientName, methods);

        sb.AppendLine("}");
        return sb.ToString();
    }

    private void EmitEnum(StringBuilder sb, EnumDecl e)
    {
        _contextTypes.Add(e.Name);
        Doc(sb, "    ", e.Doc);
        sb.Append("    [JsonConverter(typeof(JsonStringEnumConverter<").Append(e.Name).AppendLine(">))]");
        sb.Append("    public enum ").AppendLine(e.Name);
        sb.AppendLine("    {");

        foreach (var member in e.Members)
        {
            sb.Append("        [JsonStringEnumMemberName(").Append(Naming.Literal(member.Value)).AppendLine(")]");
            sb.Append("        ").Append(member.Key).AppendLine(",");
        }

        sb.AppendLine("    }");
    }

    private void EmitRecord(StringBuilder sb, RecordDecl r)
    {
        _contextTypes.Add(r.Name);
        Doc(sb, "    ", r.Doc, r.Props.Select(p => new KeyValuePair<string, string?>(p.CsName, p.Doc)));

        if (r.IsAbstract)
        {
            sb.Append("    [JsonPolymorphic(TypeDiscriminatorPropertyName = ").Append(Naming.Literal(r.DiscriminatorProperty!)).AppendLine(")]");

            foreach (var derived in r.Derived)
            {
                sb.Append("    [JsonDerivedType(typeof(").Append(derived.Key).Append("), ").Append(Naming.Literal(derived.Value)).AppendLine(")]");
            }

            sb.Append("    public abstract record ").Append(r.Name).AppendLine();
            sb.AppendLine("    {");
            sb.AppendLine("    }");
            return;
        }

        var ordered = r.Props.Where(p => p.Required).Concat(r.Props.Where(p => !p.Required)).ToList();
        sb.Append("    public sealed record ").Append(r.Name);

        if (ordered.Count == 0)
        {
            sb.Append(r.BaseName is null ? "" : " : " + r.BaseName).AppendLine();
            sb.AppendLine("    {");
            sb.AppendLine("    }");
            return;
        }

        sb.AppendLine("(");

        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            sb.Append("        [property: JsonPropertyName(").Append(Naming.Literal(p.JsonName)).Append(')');

            if (!p.Required)
                sb.Append(", JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)");

            sb.Append("] ").Append(p.Type).Append(' ').Append(p.CsName);

            if (!p.Required)
                sb.Append(" = null");

            sb.AppendLine(i < ordered.Count - 1 ? "," : ")" + (r.BaseName is null ? "" : " : " + r.BaseName) + ";");
        }
    }

    private void EmitUnion(StringBuilder sb, UnionDecl u)
    {
        _contextTypes.Add(u.Name);

        var labels = new List<string>();

        foreach (var variant in u.Variants)
        {
            var label = Naming.TypeLabel(variant);

            if (labels.Contains(label))
                label += labels.Count;

            labels.Add(label);
        }

        Doc(sb, "    ", "A value that is one of: " + string.Join(", ", u.Variants) + ". The matched variant is exposed by Index and Value.");
        sb.Append("    [JsonConverter(typeof(").Append(u.Name).AppendLine(".UnionConverter))]");
        sb.Append("    public sealed class ").AppendLine(u.Name);
        sb.AppendLine("    {");
        sb.Append("        private ").Append(u.Name).AppendLine("(int index, object? value)");
        sb.AppendLine("        {");
        sb.AppendLine("            Index = index;");
        sb.AppendLine("            Value = value;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        /// <summary>The position of the matched variant.</summary>");
        sb.AppendLine("        public int Index { get; }");
        sb.AppendLine();
        sb.AppendLine("        /// <summary>The matched value.</summary>");
        sb.AppendLine("        public object? Value { get; }");
        sb.AppendLine();

        for (var i = 0; i < u.Variants.Count; i++)
        {
            var type = u.Variants[i];
            var index = i.ToString(CultureInfo.InvariantCulture);

            sb.Append("        public static ").Append(u.Name).Append(" From").Append(labels[i]).Append('(').Append(type)
                .Append(" value) => new ").Append(u.Name).Append('(').Append(index).AppendLine(", value);");
            sb.Append("        public bool Is").Append(labels[i]).Append(" => Index == ").Append(index).AppendLine(";");
            sb.Append("        public ").Append(Nullable(type)).Append(" As").Append(labels[i]).Append(" => Index == ").Append(index)
                .Append(" ? (").Append(Nullable(type)).AppendLine(")Value : default;");
        }

        sb.AppendLine();
        sb.Append("        public sealed class UnionConverter : JsonConverter<").Append(u.Name).AppendLine(">");
        sb.AppendLine("        {");
        sb.Append("            public override ").Append(u.Name)
            .AppendLine("? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)");
        sb.AppendLine("            {");
        sb.AppendLine("                var element = JsonElement.ParseValue(ref reader);");

        for (var i = 0; i < u.Variants.Count; i++)
        {
            sb.AppendLine("                try");
            sb.AppendLine("                {");
            sb.Append("                    var v").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" = JsonSerializer.Deserialize<")
                .Append(u.Variants[i]).AppendLine(">(element, options);");
            sb.Append("                    if ((object?)v").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" is not null) return From")
                .Append(labels[i]).Append("(v").Append(i.ToString(CultureInfo.InvariantCulture)).AppendLine(");");
            sb.AppendLine("                }");
            sb.AppendLine("                catch (JsonException)");
            sb.AppendLine("                {");
            sb.AppendLine("                }");
        }

        sb.Append("                throw new JsonException(\"The value matches none of the variants of ").Append(u.Name).AppendLine(".\");");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.Append("            public override void Write(Utf8JsonWriter writer, ").Append(u.Name).AppendLine(" value, JsonSerializerOptions options)");
        sb.AppendLine("            {");
        sb.AppendLine("                switch (value.Index)");
        sb.AppendLine("                {");

        for (var i = 0; i < u.Variants.Count; i++)
        {
            sb.Append("                    case ").Append(i.ToString(CultureInfo.InvariantCulture)).Append(": JsonSerializer.Serialize(writer, (")
                .Append(u.Variants[i]).AppendLine(")value.Value!, options); break;");
        }

        sb.AppendLine("                    default: writer.WriteNullValue(); break;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private static string Signature(MethodModel m, bool streamAttribute)
    {
        var ordered = m.Params.Where(p => p.Required).Concat(m.Params.Where(p => !p.Required)).ToList();
        var args = new List<string>();

        foreach (var p in ordered)
            args.Add(p.Required ? p.Type + " " + p.CsName : Nullable(p.Type) + " " + p.CsName + " = null");

        args.Add((streamAttribute ? "[EnumeratorCancellation] " : "") + "CancellationToken cancellationToken = default");
        return string.Join(", ", args);
    }

    private static string ReturnType(MethodModel m) =>
        m.ResultType is null ? "Task"
        : m.IsStream ? "IAsyncEnumerable<" + m.ResultType + ">"
        : "Task<" + m.ResultType + ">";

    private static void EmitInterface(StringBuilder sb, string clientName, List<MethodModel> methods)
    {
        sb.Append("    /// <summary>Typed calls for the generated ").Append(Naming.XmlEscape(clientName)).AppendLine(" API.</summary>");
        sb.Append("    public interface I").Append(clientName).AppendLine();
        sb.AppendLine("    {");

        foreach (var m in methods.Where(m => !m.IsPush))
        {
            Doc(sb, "        ", m.Summary,
                m.Params.Select(p => new KeyValuePair<string, string?>(p.CsName, p.Doc)));

            sb.Append("        [JsonRpcMethod(").Append(Naming.Literal(m.Wire)).AppendLine(")]");

            if (m.ResultType is null)
                sb.AppendLine("        [JsonRpcNotification]");

            if (m.Deprecated)
                sb.AppendLine("        [Obsolete]");

            sb.Append("        ").Append(ReturnType(m)).Append(' ').Append(m.CsName).Append('(').Append(Signature(m, false)).AppendLine(");");
            sb.AppendLine();
        }

        sb.AppendLine("    }");
    }

    private void EmitClient(StringBuilder sb, string clientName, List<MethodModel> methods)
    {
        var contextName = clientName + "JsonContext";
        var defaultOptions = _settings.EmitJsonContext ? contextName + ".Default.Options" : "JsonRpcSerializer.DefaultOptions";

        sb.Append("    /// <summary>A client for the API, over any <see cref=\"JsonRpcPeer\"/> (any transport and serializer).</summary>\n");
        sb.Append("    public sealed class ").Append(clientName).Append(" : I").Append(clientName).AppendLine();
        sb.AppendLine("    {");
        sb.AppendLine("        private readonly JsonRpcPeer _peer;");
        sb.AppendLine("        private readonly JsonSerializerOptions _options;");
        sb.AppendLine("        private readonly JsonRpcParamStyle _defaultParamStyle;");
        sb.AppendLine();
        sb.Append("        public ").Append(clientName)
            .AppendLine("(JsonRpcPeer peer, JsonSerializerOptions? options = null, JsonRpcParamStyle defaultParamStyle = JsonRpcParamStyle.ByPosition)");
        sb.AppendLine("        {");
        sb.AppendLine("            _peer = peer ?? throw new ArgumentNullException(nameof(peer));");
        sb.Append("            _options = options ?? ").Append(defaultOptions).AppendLine(";");
        sb.AppendLine("            _defaultParamStyle = defaultParamStyle;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        private JsonRpcParams? BuildParams(JsonRpcParamStyle style, string[] names, object?[] values, bool[] optional)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (names.Length == 0)");
        sb.AppendLine("                return null;");
        sb.AppendLine();
        sb.AppendLine("            if (style == JsonRpcParamStyle.ByName)");
        sb.AppendLine("            {");
        sb.AppendLine("                var named = new List<KeyValuePair<string, object?>>();");
        sb.AppendLine();
        sb.AppendLine("                for (var i = 0; i < names.Length; i++)");
        sb.AppendLine("                {");
        sb.AppendLine("                    if (!(optional[i] && values[i] is null))");
        sb.AppendLine("                        named.Add(new KeyValuePair<string, object?>(names[i], values[i]));");
        sb.AppendLine("                }");
        sb.AppendLine();
        sb.AppendLine("                return JsonRpcParams.ByName(_options, named);");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            // By position: trailing optional values that were not supplied are left off.");
        sb.AppendLine("            var count = values.Length;");
        sb.AppendLine();
        sb.AppendLine("            while (count > 0 && optional[count - 1] && values[count - 1] is null)");
        sb.AppendLine("                count--;");
        sb.AppendLine();
        sb.AppendLine("            var positional = new List<object?>(count);");
        sb.AppendLine();
        sb.AppendLine("            for (var i = 0; i < count; i++)");
        sb.AppendLine("                positional.Add(values[i]);");
        sb.AppendLine();
        sb.AppendLine("            return JsonRpcParams.ByPosition(_options, positional);");
        sb.AppendLine("        }");

        if (methods.Any(m => m.IsPush))
        {
            sb.AppendLine();
            sb.AppendLine("        private static T? Pushed<T>(JsonElement? @params, int position, string name, JsonSerializerOptions options)");
            sb.AppendLine("        {");
            sb.AppendLine("            JsonElement? value = null;");
            sb.AppendLine();
            sb.AppendLine("            if (@params is { ValueKind: JsonValueKind.Array } array)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (position < array.GetArrayLength())");
            sb.AppendLine("                    value = array[position];");
            sb.AppendLine("            }");
            sb.AppendLine("            else if (@params is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var named))");
            sb.AppendLine("            {");
            sb.AppendLine("                value = named;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            return value is { ValueKind: not JsonValueKind.Null } present ? present.Deserialize<T>(options) : default;");
            sb.AppendLine("        }");
        }

        foreach (var m in methods.Where(m => m.IsPush))
        {
            sb.AppendLine();
            Doc(sb, "        ", m.Summary ?? "Handles the server-pushed notification '" + m.Wire + "'.",
                m.Params.Select(p => new KeyValuePair<string, string?>(p.CsName, p.Doc)));
            sb.AppendLine("        /// <remarks>Pushed by the server. Registering again replaces the previous handler.</remarks>");

            if (m.Deprecated)
                sb.AppendLine("        [Obsolete]");

            // An optional value-type param must be read as Nullable<T>: an unconstrained T? is still T, so an omitted
            // int would otherwise arrive as 0 instead of null.
            static string ArgumentType(ParamModel p) => p.Required ? p.Type : Nullable(p.Type);

            var handlerType = m.Params.Count == 0
                ? "Action"
                : "Action<" + string.Join(", ", m.Params.Select(ArgumentType)) + ">";

            var arguments = string.Join(", ", m.Params.Select((p, i) =>
                "Pushed<" + ArgumentType(p) + ">(p, " + i.ToString(CultureInfo.InvariantCulture) + ", " + Naming.Literal(p.Wire) + ", _options)"
                + (p.Required && !p.Type.EndsWith("?", StringComparison.Ordinal) ? "!" : "")));

            sb.Append("        public void ").Append(m.CsName).Append('(').Append(handlerType).AppendLine(" handler)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (handler is null)");
            sb.AppendLine("                throw new ArgumentNullException(nameof(handler));");
            sb.AppendLine();
            sb.Append("            _peer.OnNotification(").Append(Naming.Literal(m.Wire)).Append(", p => handler(").Append(arguments).AppendLine("));");
            sb.AppendLine("        }");
        }

        foreach (var m in methods.Where(m => !m.IsPush))
        {
            sb.AppendLine();
            sb.Append("        public ");

            sb.Append("async ");

            sb.Append(ReturnType(m)).Append(' ').Append(m.CsName).Append('(').Append(Signature(m, m.IsStream)).AppendLine(")");
            sb.AppendLine("        {");

            var style = m.Structure switch
            {
                "by-name" => "JsonRpcParamStyle.ByName",
                "by-position" => "JsonRpcParamStyle.ByPosition",
                _ => "_defaultParamStyle"
            };

            var names = string.Join(", ", m.Params.Select(p => Naming.Literal(p.Wire)));
            var values = string.Join(", ", m.Params.Select(p => p.CsName));
            var optional = string.Join(", ", m.Params.Select(p => p.Required ? "false" : "true"));
            var emptyArray = m.Params.Count == 0;

            sb.Append("            var @params = BuildParams(").Append(style).Append(", ")
                .Append(emptyArray ? "Array.Empty<string>()" : "new[] { " + names + " }").Append(", ")
                .Append(emptyArray ? "Array.Empty<object?>()" : "new object?[] { " + values + " }").Append(", ")
                .Append(emptyArray ? "Array.Empty<bool>()" : "new[] { " + optional + " }").AppendLine(");");

            var wire = Naming.Literal(m.Wire);

            if (m.IsStream)
            {
                sb.Append("            await foreach (var item in _peer.StreamAsync<JsonElement>(").Append(wire)
                    .AppendLine(", @params, cancellationToken).ConfigureAwait(false))");
                sb.Append("                yield return JsonSerializer.Deserialize<").Append(m.ResultType).Append(">(item, _options)")
                    .Append(m.ResultType!.EndsWith("?", StringComparison.Ordinal) ? "" : "!").AppendLine(";");
            }
            else if (m.ResultType is null)
            {
                sb.Append("            await _peer.NotifyAsync(").Append(wire).AppendLine(", @params, cancellationToken).ConfigureAwait(false);");
            }
            else
            {
                var body = new StringBuilder();
                body.Append("var element = await _peer.InvokeAsync(").Append(wire)
                    .AppendLine(", @params, null, cancellationToken).ConfigureAwait(false);");
                body.Append("return JsonSerializer.Deserialize<").Append(m.ResultType).Append(">(element, _options)")
                    .Append(m.ResultType.EndsWith("?", StringComparison.Ordinal) ? "" : "!").Append(';');

                if (m.Errors.Count == 0)
                {
                    foreach (var line in body.ToString().Replace("\r", "").Split('\n'))
                        sb.Append("            ").AppendLine(line);
                }
                else
                {
                    sb.AppendLine("            try");
                    sb.AppendLine("            {");

                    foreach (var line in body.ToString().Replace("\r", "").Split('\n'))
                        sb.Append("                ").AppendLine(line);

                    sb.AppendLine("            }");

                    foreach (var error in m.Errors)
                    {
                        sb.Append("            catch (JsonRpcException ex) when (ex.GetType() == typeof(JsonRpcException) && ex.Code == ")
                            .Append(error.Code.ToString(CultureInfo.InvariantCulture)).AppendLine(")");
                        sb.AppendLine("            {");
                        sb.Append("                throw new ").Append(error.ClassName).AppendLine("(ex.Message, ex.ErrorData);");
                        sb.AppendLine("            }");
                    }
                }
            }

            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
    }

    private string EmitContext(string clientName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("//     Generated by JsonCall. The System.Text.Json source generator completes this partial class.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine();
        sb.Append("namespace ").Append(_settings.Namespace).AppendLine();
        sb.AppendLine("{");

        var types = new SortedSet<string>(_contextTypes, StringComparer.Ordinal);

        foreach (var type in types)
            sb.Append("    [JsonSerializable(typeof(").Append(type).AppendLine("))]");

        sb.Append("    public partial class ").Append(clientName).AppendLine("JsonContext : JsonSerializerContext");
        sb.AppendLine("    {");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
