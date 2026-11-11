using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using JsonCall.Client;
using JsonCall.Core;
using JsonCall.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace JsonCall.Specs.Support;

/// <summary>The outcome of running the source generator over a document and compiling the result.</summary>
public sealed class BuildResult
{
    public required IReadOnlyList<(string HintName, string Text)> Sources { get; init; }

    public required IReadOnlyList<Diagnostic> GeneratorDiagnostics { get; init; }

    public required IReadOnlyList<Diagnostic> CompileDiagnostics { get; init; }

    public Assembly? Assembly { get; init; }

    public string Namespace { get; init; } = "JsonCall.Generated";

    public string AllSource => string.Join("\n", Sources.Select(s => s.Text));

    public IEnumerable<Diagnostic> Problems =>
        GeneratorDiagnostics.Concat(CompileDiagnostics).Where(d => d.Severity >= DiagnosticSeverity.Warning);

    public Type Type(string name) =>
        Assembly?.GetType(Namespace + "." + name)
        ?? throw new InvalidOperationException($"The generated type '{name}' does not exist. Compile errors: "
                                               + string.Join("; ", CompileDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(5)));
}

public static class GeneratorHarness
{
    private static readonly Lazy<MetadataReference[]> References = new(() =>
    {
        // Make sure the JsonCall assemblies are loaded so they show up in the reference list.
        _ = typeof(JsonRpcClient).Assembly;
        _ = typeof(JsonRpcMethodAttribute).Assembly;

        var trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator);

        var paths = trusted
            .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && a.Location.Length > 0).Select(a => a.Location))
            .Where(p => p.Length > 0 && File.Exists(p))
            .GroupBy(Path.GetFileName)
            .Select(g => g.First());

        return paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
    });

    public static CSharpCompilation CreateCompilation(string source = "namespace Host { public class Marker { } }") =>
        CSharpCompilation.Create(
            "Generated" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    public static GeneratorDriver CreateDriver(string json, string path, IDictionary<string, string>? metadata = null,
        bool track = false)
    {
        var text = new InMemoryAdditionalText(path, json);
        var options = new TestOptionsProvider(path, metadata);

        return CSharpGeneratorDriver.Create(
            ImmutableArray.Create(new OpenRpcClientGenerator().AsSourceGenerator()),
            new[] { text },
            new CSharpParseOptions(LanguageVersion.Latest),
            options,
            new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: track));
    }

    public static BuildResult Build(string json, string path = "api.openrpc.json", IDictionary<string, string>? metadata = null,
        bool compile = true)
    {
        var driver = CreateDriver(json, path, metadata)
            .RunGeneratorsAndUpdateCompilation(CreateCompilation(), out var output, out var generatorDiagnostics);

        var run = driver.GetRunResult();
        var sources = run.Results.SelectMany(r => r.GeneratedSources).Select(s => (s.HintName, s.SourceText.ToString())).ToList();

        var ns = metadata is not null && metadata.TryGetValue("Namespace", out var configured) ? configured : "JsonCall.Generated";

        if (!compile || sources.Count == 0)
        {
            return new BuildResult
            {
                Sources = sources,
                GeneratorDiagnostics = generatorDiagnostics,
                CompileDiagnostics = Array.Empty<Diagnostic>(),
                Namespace = ns
            };
        }

        using var stream = new MemoryStream();
        var emit = output.Emit(stream);

        Assembly? assembly = null;

        if (emit.Success)
        {
            stream.Position = 0;
            assembly = new AssemblyLoadContext(null, isCollectible: false).LoadFromStream(stream);
        }

        return new BuildResult
        {
            Sources = sources,
            GeneratorDiagnostics = generatorDiagnostics,
            CompileDiagnostics = emit.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Info).ToList(),
            Assembly = assembly,
            Namespace = ns
        };
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    private sealed class TestOptions : AnalyzerConfigOptions
    {
        private readonly IDictionary<string, string> _values;

        public TestOptions(IDictionary<string, string> values) => _values = values;

        public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
    }

    private sealed class TestOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly TestOptions _file;

        public TestOptionsProvider(string path, IDictionary<string, string>? metadata) =>
            _file = new TestOptions((metadata ?? new Dictionary<string, string>())
                .ToDictionary(p => "build_metadata.AdditionalFiles." + p.Key, p => p.Value));

        public override AnalyzerConfigOptions GlobalOptions { get; } = new TestOptions(new Dictionary<string, string>());

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _file;
    }
}
