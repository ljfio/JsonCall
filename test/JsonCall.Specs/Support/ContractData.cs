namespace JsonCall.Specs.Support;

/// <summary>
/// The checked-in protocol contracts under <c>Support/Data</c> (Language Server Protocol in <c>lsp</c>, Agent Client
/// Protocol in <c>acp</c>): an OpenRPC document and the sibling schema files it refers to.
/// </summary>
public static class ContractData
{
    /// <summary>The data directory copied next to the test assembly.</summary>
    public static string OutputDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Support", "Data");

    /// <summary>Reads a data file by its path relative to the data directory, with '/' separators.</summary>
    public static string Read(string relative) => File.ReadAllText(Resolve(OutputDirectory, relative));

    /// <summary>Reads a data file's bytes by its path relative to the data directory.</summary>
    public static byte[] ReadBytes(string relative) => File.ReadAllBytes(Resolve(OutputDirectory, relative));

    /// <summary>The data directory in the source tree, for rewriting golden files.</summary>
    public static string SourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JsonCall.slnx")))
            directory = directory.Parent;

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "test", "JsonCall.Specs", "Support", "Data");
    }

    /// <summary>Joins a '/'-separated relative path onto a directory.</summary>
    public static string Resolve(string directory, string relative) =>
        Path.Combine(new[] { directory }.Concat(relative.Split('/')).ToArray());
}
