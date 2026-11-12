using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JsonCall.Generator.Core;

namespace JsonCall.Tool;

/// <summary>The <c>jsoncall</c> command line tool.</summary>
public static class Cli
{
    private const string Usage = """
        Usage:
          jsoncall generate <document.openrpc.json> [--out <dir>] [options]
          jsoncall generate --url <endpoint> --discover [--out <dir>] [options]

        Options:
          --out <dir>            Directory for the generated files (default: ./gen)
          --namespace <name>     Namespace of the generated types
          --client-name <name>   Name of the client class (the interface is I<name>)
          --number-type decimal  Map JSON numbers to decimal instead of double
          --json-context         Also emit a JsonSerializerContext for AOT (completed by the System.Text.Json generator)
          --url <endpoint>       JSON-RPC HTTP endpoint (with --discover)
          --discover             Fetch the document by calling rpc.discover
        """;

    public static async Task<int> Main(string[] args) =>
        await RunAsync(args, Console.Out, Console.Error, CancellationToken.None);

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken,
        HttpClient? http = null)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            await output.WriteLineAsync(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        if (args[0] != "generate")
        {
            await error.WriteLineAsync($"Unknown command '{args[0]}'.");
            await error.WriteLineAsync(Usage);
            return 1;
        }

        string? file = null, url = null, outDir = "gen", ns = null, clientName = null;
        var discover = false;
        var decimals = false;
        var jsonContext = false;

        for (var i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");

            try
            {
                switch (args[i])
                {
                    case "--out": outDir = Next(); break;
                    case "--namespace": ns = Next(); break;
                    case "--client-name": clientName = Next(); break;
                    case "--url": url = Next(); break;
                    case "--number-type": decimals = Next().Equals("decimal", StringComparison.OrdinalIgnoreCase); break;
                    case "--discover": discover = true; break;
                    case "--json-context": jsonContext = true; break;
                    default:
                        if (args[i].StartsWith("--", StringComparison.Ordinal))
                        {
                            await error.WriteLineAsync($"Unknown option '{args[i]}'.");
                            return 1;
                        }

                        file = args[i];
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                await error.WriteLineAsync(ex.Message);
                return 1;
            }
        }

        string text;
        string name;

        if (discover)
        {
            if (url is null)
            {
                await error.WriteLineAsync("--discover needs --url.");
                return 1;
            }

            try
            {
                text = await DiscoverAsync(url, http, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                await error.WriteLineAsync($"Could not fetch the document from {url}: {ex.Message}");
                return 2;
            }

            name = clientName is null ? "api.openrpc.json" : clientName + ".openrpc.json";
            Directory.CreateDirectory(outDir);
            await File.WriteAllTextAsync(Path.Combine(outDir, name), text, new UTF8Encoding(false), cancellationToken);
            await output.WriteLineAsync($"Saved {Path.Combine(outDir, name)}");
        }
        else if (file is not null)
        {
            if (!File.Exists(file))
            {
                await error.WriteLineAsync($"The file '{file}' does not exist.");
                return 1;
            }

            text = await File.ReadAllTextAsync(file, cancellationToken);
            name = Path.GetFileName(file);
        }
        else
        {
            await error.WriteLineAsync("Give a document to generate from, or use --url with --discover.");
            return 1;
        }

        var settings = new GenSettings { ClientName = clientName, NumberAsDecimal = decimals, EmitJsonContext = jsonContext };

        if (ns is not null)
            settings.Namespace = ns;

        // Sibling schema files are resolved against the document's directory on disk.
        var documentPath = file is not null ? Path.GetFullPath(file) : Path.GetFullPath(Path.Combine(outDir, name));

        var result = ClientGenerator.Generate(text, documentPath, settings,
            (key, _) => File.Exists(key) ? File.ReadAllText(key) : null);
        var exit = 0;

        foreach (var diagnostic in result.Diagnostics)
        {
            var location = $"{name}({diagnostic.Line + 1},{diagnostic.Column + 1})";
            var line = $"{location}: {diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Id}: {diagnostic.Message} (JSON path: {diagnostic.Path})";

            if (diagnostic.Severity == GenSeverity.Error)
            {
                await error.WriteLineAsync(line);
                exit = 3;
            }
            else
            {
                await output.WriteLineAsync(line);
            }
        }

        if (exit != 0)
            return exit;

        Directory.CreateDirectory(outDir);

        foreach (var generated in result.Files)
        {
            var path = Path.Combine(outDir, generated.Name);
            await File.WriteAllTextAsync(path, generated.Content, new UTF8Encoding(false), cancellationToken);
            await output.WriteLineAsync($"Generated {path}");
        }

        return 0;
    }

    private static async Task<string> DiscoverAsync(string url, HttpClient? http, CancellationToken cancellationToken)
    {
        var owned = http is null;
        http ??= new HttpClient();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","method":"rpc.discover","id":1}""", Encoding.UTF8,
                    new MediaTypeHeaderValue("application/json"))
            };

            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

            if (body.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException("rpc.discover returned an error: " + error.GetRawText());

            if (!body.RootElement.TryGetProperty("result", out var result))
                throw new InvalidOperationException("The response has no result.");

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" });
        }
        finally
        {
            if (owned)
                http.Dispose();
        }
    }
}
