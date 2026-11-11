using System.Collections.Concurrent;
using System.Reflection;
using System.Xml.Linq;

namespace JsonCall.OpenRpc;

/// <summary>Reads the compiler's XML documentation file that sits next to an assembly.</summary>
internal static class XmlDocumentation
{
    private static readonly ConcurrentDictionary<Assembly, Dictionary<string, XElement>> Cache = new();

    public sealed record MethodDocs(string? Summary, string? Remarks, IReadOnlyDictionary<string, string> Params);

    public static MethodDocs? ForMethod(MethodInfo method)
    {
        var type = method.DeclaringType;

        if (type is null)
            return null;

        var members = Load(type.Assembly);

        if (members.Count == 0)
            return null;

        var prefix = "M:" + (type.FullName ?? type.Name).Replace('+', '.') + "." + method.Name;
        var arity = method.GetParameters().Length;

        var candidates = members
            .Where(m => m.Key == prefix || m.Key.StartsWith(prefix + "(", StringComparison.Ordinal))
            .ToList();

        // Overloads are told apart by parameter count; XML doc ids spell the types out, which we do not need.
        var element = candidates.Count == 1
            ? candidates[0].Value
            : candidates.FirstOrDefault(c => CountParams(c.Key) == arity).Value;

        if (element is null)
            return null;

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var param in element.Elements("param"))
        {
            if (param.Attribute("name")?.Value is { } name && Clean(param) is { } text)
                parameters[name] = text;
        }

        return new MethodDocs(Clean(element.Element("summary")), Clean(element.Element("remarks")), parameters);
    }

    private static int CountParams(string key)
    {
        var open = key.IndexOf('(');

        if (open < 0)
            return 0;

        var depth = 0;
        var count = 1;

        for (var i = open + 1; i < key.Length - 1; i++)
        {
            switch (key[i])
            {
                case '{' or '[' or '(': depth++; break;
                case '}' or ']' or ')': depth--; break;
                case ',' when depth == 0: count++; break;
            }
        }

        return key.Length - open <= 2 ? 0 : count;
    }

    private static string? Clean(XElement? element)
    {
        if (element is null)
            return null;

        var text = string.Concat(element.Nodes().Select(n => n switch
        {
            XText t => t.Value,
            XElement e when e.Attribute("cref") is { } cref => cref.Value.Split('.').Last(),
            XElement e when e.Attribute("name") is { } name => name.Value,
            XElement e => e.Value,
            _ => string.Empty
        }));

        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return text.Length == 0 ? null : text;
    }

    private static Dictionary<string, XElement> Load(Assembly assembly) =>
        Cache.GetOrAdd(assembly, static a =>
        {
            var result = new Dictionary<string, XElement>(StringComparer.Ordinal);

            try
            {
                if (a.IsDynamic || string.IsNullOrEmpty(a.Location))
                    return result;

                var path = Path.ChangeExtension(a.Location, ".xml");

                if (!File.Exists(path))
                    return result;

                foreach (var member in XDocument.Load(path).Descendants("member"))
                {
                    if (member.Attribute("name")?.Value is { } name)
                        result[name] = member;
                }
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
            {
                // Documentation is optional; a missing or broken file must not break generation.
            }

            return result;
        });
}
