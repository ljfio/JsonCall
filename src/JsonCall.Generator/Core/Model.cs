using System.Collections.Generic;

namespace JsonCall.Generator.Core;

internal enum GenSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>A problem found while generating, positioned in the OpenRPC document.</summary>
internal sealed class GenDiagnostic
{
    public GenDiagnostic(string id, GenSeverity severity, string message, string path, JNode? node)
    {
        Id = id;
        Severity = severity;
        Message = message;
        Path = path;
        Offset = node?.Offset ?? 0;
        Length = node?.Length ?? 0;
        Line = node?.Line ?? 0;
        Column = node?.Column ?? 0;
    }

    public string Id { get; }

    public GenSeverity Severity { get; }

    public string Message { get; }

    /// <summary>The JSON path, for example <c>methods[2].params[1].name</c>.</summary>
    public string Path { get; }

    public int Offset { get; }

    public int Length { get; }

    /// <summary>Zero-based.</summary>
    public int Line { get; }

    /// <summary>Zero-based.</summary>
    public int Column { get; }

    public override string ToString() => $"{Id} ({Severity}) at {Path}: {Message}";
}

internal static class DiagnosticIds
{
    public const string InvalidDocument = "JSONCALL001";
    public const string UnsupportedKeyword = "JSONCALL002";
    public const string UnsupportedVersion = "JSONCALL003";
    public const string UnionFallback = "JSONCALL004";
    public const string ParseError = "JSONCALL005";
    public const string UnresolvedReference = "JSONCALL006";
}

internal sealed class GenSettings
{
    /// <summary>The namespace of the generated types.</summary>
    public string Namespace { get; set; } = "JsonCall.Generated";

    /// <summary>The client class name; the interface is "I" + this. Defaults to the file name + "Client".</summary>
    public string? ClientName { get; set; }

    /// <summary>Map JSON "number" to decimal instead of double.</summary>
    public bool NumberAsDecimal { get; set; }

    /// <summary>
    /// Also emit a <c>JsonSerializerContext</c> declaration covering the DTOs. The System.Text.Json source generator
    /// completes it, but generators cannot see each other's output, so this is only usable from the command line tool.
    /// </summary>
    public bool EmitJsonContext { get; set; }
}

internal sealed class GenFile
{
    public GenFile(string name, string content)
    {
        Name = name;
        Content = content;
    }

    public string Name { get; }

    public string Content { get; }
}

internal sealed class GenResult
{
    public List<GenFile> Files { get; } = new();

    public List<GenDiagnostic> Diagnostics { get; } = new();

    public bool HasErrors
    {
        get
        {
            foreach (var diagnostic in Diagnostics)
            {
                if (diagnostic.Severity == GenSeverity.Error)
                    return true;
            }

            return false;
        }
    }
}
