using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace JsonCall.Generator.Core;

/// <summary>Turns an OpenRPC document into C# DTOs, a contract interface and a client class.</summary>
internal sealed partial class ClientGenerator
{
    private static readonly string[] UnsupportedKeywords = { "patternProperties", "not", "if", "propertyNames", "dependencies" };

    private readonly GenSettings _settings;
    private readonly GenResult _result = new();
    private readonly SortedDictionary<string, Decl> _decls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _componentCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PolyInfo> _polyBase = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedKeywords = new(StringComparer.Ordinal);
    private readonly List<string> _contextTypes = new();

    private JNode _root = null!;
    private JNode? _schemas;
    private string _fileName = "api";

    private ClientGenerator(GenSettings settings) => _settings = settings;

    public static GenResult Generate(string text, string fileName, GenSettings settings, DocumentLoader? loader = null)
    {
        var generator = new ClientGenerator(settings) { _fileName = fileName, _loader = loader };

        try
        {
            generator._root = JsonParser.Parse(text);
        }
        catch (JsonParseException ex)
        {
            var at = new JNode(JKind.Null, ex.Offset, ex.Line, ex.Column) { Length = 1 };
            generator._result.Diagnostics.Add(new GenDiagnostic(DiagnosticIds.ParseError, GenSeverity.Error,
                "The OpenRPC document is not valid JSON: " + ex.Message, "$", at));
            return generator._result;
        }

        generator.Run();
        return generator._result;
    }

    private void Warn(string id, GenSeverity severity, string message, string path, JNode? node)
    {
        // Inside a referenced document the positions mean nothing in the main file, so point at the $ref that led there.
        if (_anchor is not null)
        {
            path = _anchorPath + " -> " + path;
            node = _anchor;
        }

        _result.Diagnostics.Add(new GenDiagnostic(id, severity, message, path, node));
    }

    // ---- declarations -------------------------------------------------

    private abstract class Decl
    {
        public string Name = "";
        public string? Doc;
    }

    private sealed class Prop
    {
        public string JsonName = "";
        public string CsName = "";
        public string Type = "";
        public bool Required;
        public string? Doc;
    }

    private sealed class RecordDecl : Decl
    {
        public readonly List<Prop> Props = new();
        public string? BaseName;
        public bool IsAbstract;
        public string? DiscriminatorProperty;
        public readonly List<KeyValuePair<string, string>> Derived = new(); // type name, discriminator value
    }

    private sealed class EnumDecl : Decl
    {
        public readonly List<KeyValuePair<string, string>> Members = new(); // C# name, JSON name
    }

    private sealed class UnionDecl : Decl
    {
        public readonly List<string> Variants = new();
    }

    private sealed class PolyInfo
    {
        public string BaseName = "";
        public string Property = "";
        public string Value = "";
    }

    private string Unique(string candidate)
    {
        var name = Naming.Pascal(candidate);
        var result = name;

        for (var i = 2; !_usedNames.Add(result); i++)
            result = name + i;

        return result;
    }

    // ---- driver -------------------------------------------------------

    private void Run()
    {
        if (!_root.IsObject || _root.Get("methods") is not { IsArray: true } methods)
        {
            Warn(DiagnosticIds.InvalidDocument, GenSeverity.Error,
                "An OpenRPC document must be an object with a \"methods\" array.", "methods", _root.Get("methods") ?? _root);
            return;
        }

        CheckVersion();

        var methodList = new List<MethodModel>();

        if (!ValidateMethods(methods))
            return;

        _schemas = _root.Get("components")?.Get("schemas");

        // Component names are reserved first so inline types never take them.
        if (_schemas?.Members is { } members)
        {
            foreach (var member in members)
                _usedNames.Add(Naming.Pascal(member.Key));

            PreRegisterPolymorphicBases(members);
        }

        var clientName = _settings.ClientName ?? DeriveClientName();
        _usedNames.Add(clientName);
        _usedNames.Add("I" + clientName);

        if (_schemas?.Members is { } all)
        {
            foreach (var member in all)
                MapComponent(member.Key);
        }

        for (var i = 0; i < methods.Items!.Count; i++)
            methodList.Add(BuildMethod(ResolveMethod(methods.Items[i], "methods[" + i + "]", out var path), path));

        if (_result.HasErrors)
            return;

        var fileBase = clientName;
        _result.Files.Add(new GenFile(fileBase + ".g.cs", Emit(clientName, methodList)));

        if (_settings.EmitJsonContext)
            _result.Files.Add(new GenFile(fileBase + ".JsonContext.g.cs", EmitContext(clientName)));
    }

    private string DeriveClientName()
    {
        var name = _fileName;
        var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));

        if (slash >= 0)
            name = name.Substring(slash + 1);

        foreach (var suffix in new[] { ".openrpc.json", "-openrpc.json", "_openrpc.json", ".json" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - suffix.Length);
                break;
            }
        }

        var pascal = string.IsNullOrWhiteSpace(name) ? "Api" : Naming.Pascal(name);
        return pascal.EndsWith("Client", StringComparison.Ordinal) ? pascal : pascal + "Client";
    }

    private void CheckVersion()
    {
        var version = _root.Get("openrpc");
        var text = version?.Kind == JKind.String ? version.Text! : null;
        var supported = false;

        if (text is not null)
        {
            var parts = text.Split('.', '-');

            supported = parts.Length >= 2 && parts[0] == "1" && int.TryParse(parts[1], out var minor) && minor <= 4;
        }

        if (!supported)
        {
            Warn(DiagnosticIds.UnsupportedVersion, GenSeverity.Warning,
                $"The OpenRPC version '{text ?? "(missing)"}' is not supported. Supported versions are 1.0 to 1.4. Generation continues on a best-effort basis.",
                "openrpc", version ?? _root);
        }
    }

    private bool ValidateMethods(JNode methods)
    {
        var ok = true;
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < methods.Items!.Count; i++)
        {
            var method = ResolveMethod(methods.Items[i], "methods[" + i + "]", out var path);

            if (!method.IsObject || method.GetString("name") is not { Length: > 0 } name)
            {
                Warn(DiagnosticIds.InvalidDocument, GenSeverity.Error, "A method needs a \"name\".", path + ".name", method);
                ok = false;
                continue;
            }

            if (!names.Add(name))
            {
                Warn(DiagnosticIds.InvalidDocument, GenSeverity.Error, $"The method name '{name}' is declared more than once.",
                    path + ".name", method.Get("name"));
                ok = false;
            }

            if (method.Get("params") is { IsArray: true } parameters)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);

                for (var j = 0; j < parameters.Items!.Count; j++)
                {
                    var parameter = ResolveDescriptor(parameters.Items[j], path + ".params[" + j + "]", out var parameterPath);

                    if (parameter.GetString("name") is not { } parameterName)
                    {
                        Warn(DiagnosticIds.InvalidDocument, GenSeverity.Error, "A parameter needs a \"name\".",
                            parameterPath + ".name", parameter);
                        ok = false;
                    }
                    else if (!seen.Add(parameterName))
                    {
                        Warn(DiagnosticIds.InvalidDocument, GenSeverity.Error,
                            $"The parameter name '{parameterName}' is declared more than once in method '{name}'.",
                            path + ".params[" + j + "].name", parameter.Get("name"));
                        ok = false;
                    }
                }
            }
        }

        return ok;
    }

    private JNode ResolveMethod(JNode method, string path, out string resolvedPath)
    {
        resolvedPath = path;

        if (method.IsObject && method.GetString("$ref") is { } reference
            && Lookup(reference, "#/components/methods/", "components", "methods") is { } target)
        {
            resolvedPath = "components.methods." + reference.Substring("#/components/methods/".Length);
            return target;
        }

        return method;
    }

    private JNode ResolveDescriptor(JNode node, string path, out string resolvedPath)
    {
        resolvedPath = path;

        if (node.IsObject && node.GetString("$ref") is { } reference
            && Lookup(reference, "#/components/contentDescriptors/", "components", "contentDescriptors") is { } target)
        {
            resolvedPath = "components.contentDescriptors." + reference.Substring("#/components/contentDescriptors/".Length);
            return target;
        }

        return node;
    }

    private JNode? Lookup(string reference, string prefix, string section, string group)
    {
        if (!reference.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        return _root.Get(section)?.Get(group)?.Get(reference.Substring(prefix.Length));
    }

    // ---- schema mapping -----------------------------------------------

    private static string Nullable(string type) => type.EndsWith("?", StringComparison.Ordinal) ? type : type + "?";

    private string MapComponent(string name)
    {
        var reference = "#/components/schemas/" + name;

        if (_componentCache.TryGetValue(reference, out var cached))
            return cached;

        var target = _schemas?.Get(name);

        if (target is null)
            return "JsonElement";

        var declared = Naming.Pascal(name);

        // Provisional answer so that a cyclic reference finds the type being built.
        _componentCache[reference] = declared;

        var mapped = Map(target, "components.schemas." + name, declared, declared);
        _componentCache[reference] = mapped;
        return mapped;
    }

    private string Map(JNode schema, string path, string hint, string? declared = null)
    {
        if (!schema.IsObject)
            return "JsonElement";

        foreach (var keyword in UnsupportedKeywords)
        {
            if (schema.Has(keyword))
            {
                var key = path + "." + keyword;

                if (_reportedKeywords.Add(key))
                {
                    Warn(DiagnosticIds.UnsupportedKeyword, GenSeverity.Warning,
                        $"The JSON Schema keyword '{keyword}' is not supported; the value is generated as JsonElement.",
                        key, schema.Get(keyword));
                }

                return "JsonElement";
            }
        }

        if (schema.GetString("$ref") is { } reference)
        {
            if (IsOwnComponent(reference))
            {
                var name = reference.Substring("#/components/schemas/".Length);

                if (_schemas?.Has(name) == true)
                    return MapComponent(name);
            }

            if (!IsOwnComponent(reference) && MapExternalReference(schema, reference, path, hint) is { } external)
                return external;

            if (IsOwnComponent(reference))
            {
                Warn(DiagnosticIds.UnresolvedReference, GenSeverity.Warning,
                    $"The reference '{reference}' cannot be resolved; the value is generated as JsonElement.", path + ".$ref",
                    schema.Get("$ref"));
            }

            return "JsonElement";
        }

        var nullable = false;
        var types = new List<string>();

        if (schema.Get("type") is { } typeNode)
        {
            if (typeNode.IsString)
                types.Add(typeNode.Text!);
            else if (typeNode.IsArray)
                types.AddRange(typeNode.Items!.Where(i => i.IsString).Select(i => i.Text!));
        }

        if (types.Remove("null"))
            nullable = true;

        string result;

        if (schema.Get("allOf") is { IsArray: true } allOf)
            result = MapAllOf(schema, allOf, path, hint, declared);
        else if (schema.Get("oneOf") is { IsArray: true } oneOf)
            result = MapUnion(oneOf, path + ".oneOf", hint, declared, ref nullable);
        else if (schema.Get("anyOf") is { IsArray: true } anyOf)
            result = MapUnion(anyOf, path + ".anyOf", hint, declared, ref nullable);
        else if (schema.Get("enum") is { IsArray: true } enumNode)
            result = MapEnum(schema, enumNode, types, path, hint, declared, ref nullable);
        else if (types.Count > 1)
            result = "JsonElement";
        else if (types.Count == 1)
            result = MapByType(schema, types[0], path, hint, declared);
        else if (schema.Has("properties"))
            result = MapObject(schema, path, hint, declared);
        else
            result = "JsonElement";

        return nullable ? Nullable(result) : result;
    }

    private string MapByType(JNode schema, string type, string path, string hint, string? declared)
    {
        switch (type)
        {
            case "string":
                if (schema.GetString("contentEncoding") == "base64")
                    return "byte[]";

                return schema.GetString("format") switch
                {
                    "date-time" => "DateTimeOffset",
                    "uuid" => "Guid",
                    "date" => "DateOnly",
                    "time" => "TimeOnly",
                    "uri" => "Uri",
                    _ => "string"
                };

            case "integer":
                return schema.GetString("format") switch
                {
                    "int32" => "int",
                    "int64" => "long",
                    _ => "long"
                };

            case "number":
                return schema.GetString("format") switch
                {
                    "float" => "float",
                    "double" => "double",
                    _ => _settings.NumberAsDecimal ? "decimal" : "double"
                };

            case "boolean":
                return "bool";

            case "array":
            {
                if (schema.Get("items") is { IsObject: true } items)
                    return "IReadOnlyList<" + Map(items, path + ".items", hint + "Item") + ">";

                return "IReadOnlyList<JsonElement>";
            }

            case "object":
                return MapObject(schema, path, hint, declared);

            default:
                return "JsonElement";
        }
    }

    private string MapObject(JNode schema, string path, string hint, string? declared)
    {
        if (schema.Get("properties") is { IsObject: true, Members.Count: > 0 })
        {
            var name = declared ?? Unique(hint);
            var record = new RecordDecl { Name = name, Doc = Description(schema) };
            _decls[name] = record;
            AddProperties(record, schema, path, null);
            ApplyPolymorphism(record, declared);
            return name;
        }

        if (schema.Get("additionalProperties") is { IsObject: true } additional)
            return "IReadOnlyDictionary<string, " + Map(additional, path + ".additionalProperties", hint + "Value") + ">";

        if (declared is not null && schema.Get("properties") is { IsObject: true })
        {
            var empty = new RecordDecl { Name = declared, Doc = Description(schema) };
            _decls[declared] = empty;
            ApplyPolymorphism(empty, declared);
            return declared;
        }

        return "JsonElement";
    }

    private void ApplyPolymorphism(RecordDecl record, string? declared)
    {
        if (declared is not null && _polyBase.TryGetValue(declared, out var poly))
            record.BaseName = poly.BaseName;
    }

    private void AddProperties(RecordDecl record, JNode schema, string path, string? skipProperty)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);

        if (schema.Get("required") is { IsArray: true } requiredNode)
        {
            foreach (var item in requiredNode.Items!.Where(i => i.IsString))
                required.Add(item.Text!);
        }

        if (schema.Get("properties") is not { IsObject: true } properties)
            return;

        var used = new HashSet<string>(record.Props.Select(p => p.CsName), StringComparer.Ordinal) { record.Name };

        foreach (var member in properties.Members!)
        {
            if (member.Key == skipProperty)
                continue;

            var propertyName = Naming.Pascal(member.Key);
            var csName = propertyName;

            for (var i = 2; !used.Add(csName); i++)
                csName = propertyName + i;

            var type = Map(member.Value, path + ".properties." + member.Key, record.Name + Naming.Pascal(member.Key));
            var isRequired = required.Contains(member.Key);

            if (!isRequired)
                type = Nullable(type);

            // Replace a property of the same JSON name (allOf can repeat one).
            record.Props.RemoveAll(p => p.JsonName == member.Key);

            record.Props.Add(new Prop
            {
                JsonName = member.Key,
                CsName = csName,
                Type = type,
                Required = isRequired,
                Doc = Description(member.Value)
            });
        }
    }

    private string MapAllOf(JNode schema, JNode allOf, string path, string hint, string? declared)
    {
        var name = declared ?? Unique(hint);
        var record = new RecordDecl { Name = name, Doc = Description(schema) };
        _decls[name] = record;

        for (var i = 0; i < allOf.Items!.Count; i++)
            CollectInto(record, allOf.Items[i], path + ".allOf[" + i + "]", new HashSet<string>(StringComparer.Ordinal));

        if (schema.Has("properties"))
            AddProperties(record, schema, path, null);

        ApplyPolymorphism(record, declared);
        return name;
    }

    private void CollectInto(RecordDecl record, JNode part, string path, HashSet<string> visited)
    {
        if (part.GetString("$ref") is { } reference)
        {
            var key = IsOwnComponent(reference) ? reference : CurrentKey + "|" + reference;

            if (ResolveReferenceNode(reference, out var document, part, path) is { } target && visited.Add(key))
            {
                var previous = _current;
                _current = document ?? (IsOwnComponent(reference) ? null : _current);

                try
                {
                    CollectInto(record, target, IsOwnComponent(reference) ? "components.schemas." + reference.Substring("#/components/schemas/".Length) : path, visited);
                }
                finally
                {
                    _current = previous;
                }
            }

            return;
        }

        if (part.Get("allOf") is { IsArray: true } nested)
        {
            for (var i = 0; i < nested.Items!.Count; i++)
                CollectInto(record, nested.Items[i], path + ".allOf[" + i + "]", visited);
        }

        AddProperties(record, part, path, null);
    }

    private string MapEnum(JNode schema, JNode values, List<string> types, string path, string hint, string? declared,
        ref bool nullable)
    {
        var strings = new List<string>();
        var allStrings = true;

        foreach (var value in values.Items!)
        {
            if (value.IsString)
                strings.Add(value.Text!);
            else if (value.Kind == JKind.Null)
                nullable = true;
            else
                allStrings = false;
        }

        if (!allStrings || strings.Count == 0)
            return types.Count == 1 ? MapByType(schema, types[0], path, hint, declared) : "JsonElement";

        var name = declared ?? Unique(hint);
        var decl = new EnumDecl { Name = name, Doc = Description(schema) };
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in strings)
        {
            var member = value.Length == 0 ? "Empty" : Naming.Pascal(value);
            var candidate = member;

            for (var i = 2; !used.Add(candidate); i++)
                candidate = member + i;

            decl.Members.Add(new KeyValuePair<string, string>(candidate, value));
        }

        _decls[name] = decl;
        return name;
    }

    private JNode? ResolveSchema(JNode node)
    {
        if (node.GetString("$ref") is { } reference)
            return ResolveReferenceNode(reference, out _);

        return node;
    }

    private static string? ConstValue(JNode property)
    {
        if (property.Get("const") is { IsString: true } constant)
            return constant.Text;

        if (property.Get("enum") is { IsArray: true, Items.Count: 1 } single && single.Items[0].IsString)
            return single.Items[0].Text;

        return null;
    }

    /// <summary>Finds a property every variant pins to a constant. That property discriminates the variants.</summary>
    private string? FindDiscriminator(List<JNode> variants, out List<string> values)
    {
        values = new List<string>();

        var resolved = variants.Select(ResolveSchema).ToList();

        if (resolved.Count < 2 || resolved.Any(v => v is null || v.Get("properties") is not { IsObject: true }))
            return null;

        foreach (var candidate in resolved[0]!.Get("properties")!.Members!.Select(m => m.Key))
        {
            var found = new List<string>();

            foreach (var variant in resolved)
            {
                var property = variant!.Get("properties")!.Get(candidate);

                if (property is null || ConstValue(property) is not { } value)
                    break;

                found.Add(value);
            }

            if (found.Count == resolved.Count && found.Distinct(StringComparer.Ordinal).Count() == found.Count)
            {
                values = found;
                return candidate;
            }
        }

        return null;
    }

    private void PreRegisterPolymorphicBases(List<KeyValuePair<string, JNode>> components)
    {
        foreach (var component in components)
        {
            var node = component.Value;
            var variants = (node.Get("oneOf") ?? node.Get("anyOf")) is { IsArray: true } list
                ? list.Items!.Where(i => !IsNullSchema(i)).ToList()
                : null;

            if (variants is null || FindDiscriminator(variants, out var values) is not { } property)
                continue;

            var baseName = Naming.Pascal(component.Key);
            var allRefs = variants.All(v => v.GetString("$ref")?.StartsWith("#/components/schemas/", StringComparison.Ordinal) == true);

            if (!allRefs)
                continue;

            for (var i = 0; i < variants.Count; i++)
            {
                var name = variants[i].GetString("$ref")!.Substring("#/components/schemas/".Length);
                _polyBase[Naming.Pascal(name)] = new PolyInfo { BaseName = baseName, Property = property, Value = values[i] };
            }
        }
    }

    private static bool IsNullSchema(JNode node) =>
        node.IsObject && node.Get("type") is { IsString: true } type && type.Text == "null";

    private string MapUnion(JNode variantsNode, string path, string hint, string? declared, ref bool nullable)
    {
        var variants = new List<JNode>();

        foreach (var variant in variantsNode.Items!)
        {
            if (IsNullSchema(variant))
                nullable = true;
            else
                variants.Add(variant);
        }

        if (variants.Count == 0)
            return "JsonElement";

        if (variants.Count == 1)
            return Map(variants[0], path + "[0]", hint, declared);

        var discriminator = FindDiscriminator(variants, out var values);
        var allRefs = variants.All(v => v.GetString("$ref")?.StartsWith("#/components/schemas/", StringComparison.Ordinal) == true);

        if (discriminator is not null && (declared is null ? !allRefs : true))
        {
            // A union written inline can only be polymorphic when it owns its variants (inline objects).
            if (declared is not null || variants.All(v => v.Has("properties")))
                return BuildPolymorphic(variants, values, discriminator, path, hint, declared);
        }

        if (discriminator is not null && declared is not null)
            return BuildPolymorphic(variants, values, discriminator, path, hint, declared);

        var types = new List<string>();

        for (var i = 0; i < variants.Count; i++)
        {
            var type = Map(variants[i], path + "[" + i + "]", hint + "Option" + (i + 1));

            if (!types.Contains(type))
                types.Add(type);
        }

        if (types.Count == 1)
            return types[0];

        var name = declared ?? Unique(hint);
        var union = new UnionDecl { Name = name };
        union.Variants.AddRange(types.Select(t => t.TrimEnd('?')).Distinct());
        _decls[name] = union;

        Warn(DiagnosticIds.UnionFallback, GenSeverity.Info,
            $"The schema at '{path}' has no discriminator, so it is generated as the union wrapper '{name}'.",
            path, variantsNode);

        return name;
    }

    private string BuildPolymorphic(List<JNode> variants, List<string> values, string discriminator, string path, string hint,
        string? declared)
    {
        var baseName = declared ?? Unique(hint);
        var baseDecl = new RecordDecl { Name = baseName, IsAbstract = true, DiscriminatorProperty = discriminator };
        _decls[baseName] = baseDecl;

        for (var i = 0; i < variants.Count; i++)
        {
            var variant = variants[i];
            string derivedName;

            if (variant.GetString("$ref") is not null)
            {
                derivedName = Map(variant, path + "[" + i + "]", baseName + Naming.Pascal(values[i]));
            }
            else
            {
                derivedName = Unique(baseName + Naming.Pascal(values[i]));
                var record = new RecordDecl { Name = derivedName, BaseName = baseName, Doc = Description(variant) };
                _decls[derivedName] = record;
                AddProperties(record, variant, path + "[" + i + "]", discriminator);
            }

            if (_decls.TryGetValue(derivedName, out var derived) && derived is RecordDecl derivedRecord)
            {
                derivedRecord.BaseName = baseName;
                derivedRecord.Props.RemoveAll(p => p.JsonName == discriminator);
            }

            baseDecl.Derived.Add(new KeyValuePair<string, string>(derivedName, values[i]));
        }

        return baseName;
    }

    private static string? Description(JNode schema)
    {
        if (!schema.IsObject)
            return null;

        var text = schema.GetString("description") ?? schema.GetString("summary") ?? schema.GetString("title");
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
