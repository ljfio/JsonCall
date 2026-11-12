using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JsonCall.Generator.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace JsonCall.Generator;

/// <summary>
/// Generates a typed client from every <c>*.openrpc.json</c>, <c>*-openrpc.json</c> or <c>openrpc.json</c> in AdditionalFiles.
/// </summary>
/// <remarks>
/// A source generator only sees AdditionalFiles, so a document that refers to sibling schema files
/// (<c>{"$ref": "event.schema.json"}</c>) is resolved against the other <c>.json</c> AdditionalFiles: first by the path
/// relative to the document, then by a unique trailing path, then by a unique file name. Consumers must add the
/// sibling schemas as AdditionalFiles too.
/// </remarks>
[Generator]
public sealed class OpenRpcClientGenerator : IIncrementalGenerator
{
    /// <summary>The name of the pipeline step that reads the inputs; tests use it to check incrementality.</summary>
    public const string InputStepName = "OpenRpcInput";

    private static readonly Dictionary<string, DiagnosticDescriptor> Descriptors = new();

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Sibling schemas: every other .json AdditionalFile. Their text is part of each input, so editing one regenerates.
        var siblings = context.AdditionalTextsProvider
            .Where(static text => IsSiblingCandidate(text.Path))
            .Select(static (text, cancellationToken) => new Sibling(text.Path, text.GetText(cancellationToken)?.ToString() ?? string.Empty))
            .Collect();

        var inputs = context.AdditionalTextsProvider
            .Where(static text => IsOpenRpcFile(text.Path))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, cancellationToken) => ReadInput(pair.Left, pair.Right, cancellationToken))
            .Combine(siblings)
            .Select(static (pair, _) => pair.Left.WithSiblings(pair.Right))
            .WithTrackingName(InputStepName);

        context.RegisterSourceOutput(inputs, static (output, input) => Generate(output, input));
    }

    private static bool IsOpenRpcFile(string path)
    {
        var name = Path.GetFileName(path);

        return name.EndsWith(".openrpc.json", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("-openrpc.json", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith("_openrpc.json", StringComparison.OrdinalIgnoreCase)
               || name.Equals("openrpc.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSiblingCandidate(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !IsOpenRpcFile(path);

    private static Input ReadInput(AdditionalText text, AnalyzerConfigOptionsProvider options, CancellationToken cancellationToken)
    {
        var file = options.GetOptions(text);

        string? Metadata(string name) =>
            file.TryGetValue("build_metadata.AdditionalFiles." + name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        return new Input(
            text.Path,
            text.GetText(cancellationToken)?.ToString() ?? string.Empty,
            Metadata("Namespace"),
            Metadata("ClientName"),
            string.Equals(Metadata("NumberType"), "decimal", StringComparison.OrdinalIgnoreCase),
            string.Equals(Metadata("JsonContext"), "true", StringComparison.OrdinalIgnoreCase));
    }

    private static void Generate(SourceProductionContext output, Input input)
    {
        var settings = new GenSettings
        {
            ClientName = input.ClientName,
            NumberAsDecimal = input.NumberAsDecimal,
            EmitJsonContext = input.JsonContext
        };

        if (input.Namespace is not null)
            settings.Namespace = input.Namespace;

        var documents = new SiblingDocuments(input.Siblings.Select(s => new KeyValuePair<string, string>(s.Path, s.Text)));
        var result = ClientGenerator.Generate(input.Text, input.Path, settings, documents.Load);

        var text = SourceText.From(input.Text, Encoding.UTF8);

        foreach (var diagnostic in result.Diagnostics)
            output.ReportDiagnostic(ToDiagnostic(diagnostic, input.Path, text));

        var prefix = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(input.Path));

        foreach (var file in result.Files)
            output.AddSource(prefix + "." + file.Name, SourceText.From(file.Content, Encoding.UTF8));
    }

    private static Diagnostic ToDiagnostic(GenDiagnostic diagnostic, string path, SourceText text)
    {
        var severity = diagnostic.Severity switch
        {
            GenSeverity.Error => DiagnosticSeverity.Error,
            GenSeverity.Warning => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Info
        };

        DiagnosticDescriptor descriptor;

        lock (Descriptors)
        {
            var key = diagnostic.Id + severity;

            if (!Descriptors.TryGetValue(key, out descriptor!))
            {
                descriptor = new DiagnosticDescriptor(diagnostic.Id, "OpenRPC client generation", "{0}", "JsonCall", severity, true);
                Descriptors[key] = descriptor;
            }
        }

        var length = Math.Max(1, Math.Min(diagnostic.Length, Math.Max(0, text.Length - diagnostic.Offset)));
        var start = Math.Min(diagnostic.Offset, Math.Max(0, text.Length - 1));
        var span = new TextSpan(start, Math.Min(length, Math.Max(0, text.Length - start)));
        var location = Location.Create(path, span, text.Lines.GetLinePositionSpan(span));

        return Diagnostic.Create(descriptor, location, diagnostic.Message + " (JSON path: " + diagnostic.Path + ")");
    }

    private sealed class Sibling : IEquatable<Sibling>
    {
        public Sibling(string path, string text)
        {
            Path = path;
            Text = text;
        }

        public string Path { get; }

        public string Text { get; }

        public bool Equals(Sibling? other) => other is not null && Path == other.Path && Text == other.Text;

        public override bool Equals(object? obj) => Equals(obj as Sibling);

        public override int GetHashCode() => Path.GetHashCode() ^ Text.GetHashCode();
    }

    private sealed class Input : IEquatable<Input>
    {
        public Input(string path, string text, string? @namespace, string? clientName, bool numberAsDecimal, bool jsonContext,
            ImmutableArray<Sibling> siblings = default)
        {
            Path = path;
            Text = text;
            Namespace = @namespace;
            ClientName = clientName;
            NumberAsDecimal = numberAsDecimal;
            JsonContext = jsonContext;
            Siblings = siblings.IsDefault ? ImmutableArray<Sibling>.Empty : siblings;
        }

        public string Path { get; }

        public string Text { get; }

        public string? Namespace { get; }

        public string? ClientName { get; }

        public bool NumberAsDecimal { get; }

        public bool JsonContext { get; }

        public ImmutableArray<Sibling> Siblings { get; }

        public Input WithSiblings(ImmutableArray<Sibling> siblings) =>
            new(Path, Text, Namespace, ClientName, NumberAsDecimal, JsonContext, siblings);

        public bool Equals(Input? other) =>
            other is not null && Path == other.Path && Text == other.Text && Namespace == other.Namespace
            && ClientName == other.ClientName && NumberAsDecimal == other.NumberAsDecimal && JsonContext == other.JsonContext
            && Siblings.SequenceEqual(other.Siblings);

        public override bool Equals(object? obj) => Equals(obj as Input);

        public override int GetHashCode() => Text.GetHashCode() ^ Path.GetHashCode();
    }
}
