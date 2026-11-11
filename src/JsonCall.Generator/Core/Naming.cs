using System;
using System.Collections.Generic;
using System.Text;

namespace JsonCall.Generator.Core;

internal static class Naming
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while"
    };

    /// <summary>PascalCase from any text: "get_balance", "getBalance" and "get-balance" all give "GetBalance".</summary>
    public static string Pascal(string text)
    {
        var builder = new StringBuilder();
        var upperNext = true;

        foreach (var c in text)
        {
            if (!char.IsLetterOrDigit(c))
            {
                upperNext = true;
                continue;
            }

            builder.Append(upperNext ? char.ToUpperInvariant(c) : c);
            upperNext = false;
        }

        if (builder.Length == 0)
            return "Value";

        if (char.IsDigit(builder[0]))
            builder.Insert(0, '_');

        return builder.ToString();
    }

    public static string Camel(string text)
    {
        var pascal = Pascal(text);
        var camel = char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
        return Keywords.Contains(camel) ? "@" + camel : camel;
    }

    public static string Escape(string identifier) => Keywords.Contains(identifier) ? "@" + identifier : identifier;

    public static string Literal(string text)
    {
        var builder = new StringBuilder("\"");

        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ')
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        builder.Append(c);

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    public static string XmlEscape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>A label for a C# type, usable inside an identifier: IReadOnlyList&lt;string&gt; gives StringList.</summary>
    public static string TypeLabel(string type)
    {
        type = type.TrimEnd('?');

        if (type.EndsWith("[]", StringComparison.Ordinal))
            return "Array" + TypeLabel(type.Substring(0, type.Length - 2));

        var open = type.IndexOf('<');

        if (open > 0)
        {
            var inner = type.Substring(open + 1, type.Length - open - 2);
            var parts = inner.Split(',');
            var label = new StringBuilder();

            foreach (var part in parts)
                label.Append(TypeLabel(part.Trim()));

            var outer = type.Substring(0, open);
            return label + (outer.Contains("Dictionary") ? "Map" : "List");
        }

        return Pascal(type);
    }
}
