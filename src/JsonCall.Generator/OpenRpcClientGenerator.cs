using System;
using System.Collections.Generic;
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
[Generator]
public sealed class OpenRpcClientGenerator : IIncrementalGenerator
{
    /// <summary>The name of the pipeline step that reads the inputs; tests use it to check incrementality.</summary>
    public const string InputStepName = "OpenRpcInput";

    private static readonly Dictionary<string, DiagnosticDescriptor> Descriptors = new();

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var inputs = context.AdditionalTextsProvider
            .Where(static text => IsOpenRpcFile(text.Path))
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, cancellationToken) => ReadInput(pair.Left, pair.Right, cancellationToken))
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

        var result = ClientGenerator.Generate(input.Text, input.Path, settings);

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

    private sealed class Input : IEquatable<Input>
    {
        public Input(string path, string text, string? @namespace, string? clientName, bool numberAsDecimal, bool jsonContext)
        {
            Path = path;
            Text = text;
            Namespace = @namespace;
            ClientName = clientName;
            NumberAsDecimal = numberAsDecimal;
            JsonContext = jsonContext;
        }

        public string Path { get; }

        public string Text { get; }

        public string? Namespace { get; }

        public string? ClientName { get; }

        public bool NumberAsDecimal { get; }

        public bool JsonContext { get; }

        public bool Equals(Input? other) =>
            other is not null && Path == other.Path && Text == other.Text && Namespace == other.Namespace
            && ClientName == other.ClientName && NumberAsDecimal == other.NumberAsDecimal && JsonContext == other.JsonContext;

        public override bool Equals(object? obj) => Equals(obj as Input);

        public override int GetHashCode() => Text.GetHashCode() ^ Path.GetHashCode();
    }
}
