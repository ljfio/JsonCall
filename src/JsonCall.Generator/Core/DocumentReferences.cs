using System;
using System.Collections.Generic;
using System.Text;

namespace JsonCall.Generator.Core;

/// <summary>
/// Supplies the text of a document that an OpenRPC document refers to, for example <c>event.schema.json</c>.
/// <paramref name="key"/> is the normalised path of the referring document's directory combined with the reference;
/// <paramref name="reference"/> is the reference as written (without any <c>#</c> pointer).
/// Returns null when the document cannot be found.
/// </summary>
internal delegate string? DocumentLoader(string key, string reference);

/// <summary>Path and JSON Pointer helpers for resolving references between documents. Pure string work, no file system.</summary>
internal static class DocumentPaths
{
    /// <summary>Uses '/' separators and collapses "." and ".." segments.</summary>
    public static string Normalize(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        var result = new List<string>();

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];

            if (part == "." || (part.Length == 0 && i > 0))
                continue;

            if (part == ".." && result.Count > 0 && result[result.Count - 1] != ".." && !IsRoot(result, result.Count - 1))
                result.RemoveAt(result.Count - 1);
            else
                result.Add(part);
        }

        return string.Join("/", result);
    }

    private static bool IsRoot(List<string> segments, int index) =>
        index == 0 && (segments[0].Length == 0 || (segments[0].Length == 2 && segments[0][1] == ':'));

    /// <summary>The key of <paramref name="reference"/> relative to the directory of <paramref name="baseKey"/>.</summary>
    public static string Combine(string baseKey, string reference)
    {
        var normalized = reference.Replace('\\', '/');

        if (normalized.StartsWith("/", StringComparison.Ordinal))
            return Normalize(normalized);

        var slash = baseKey.LastIndexOf('/');
        return Normalize(slash < 0 ? normalized : baseKey.Substring(0, slash + 1) + normalized);
    }

    public static bool IsRemote(string reference) =>
        reference.IndexOf("://", StringComparison.Ordinal) > 0 || reference.StartsWith("urn:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits "file.json#/a/b" into the file part and the pointer part (without the '#').</summary>
    public static void SplitReference(string reference, out string file, out string pointer)
    {
        var hash = reference.IndexOf('#');
        file = hash < 0 ? reference : reference.Substring(0, hash);
        pointer = hash < 0 ? string.Empty : reference.Substring(hash + 1);
    }

    /// <summary>Follows an RFC 6901 JSON Pointer ("" is the root). Returns null when it does not lead anywhere.</summary>
    public static JNode? Follow(JNode root, string pointer)
    {
        if (pointer.Length == 0)
            return root;

        if (pointer[0] != '/')
            return null;

        var node = root;

        foreach (var raw in pointer.Substring(1).Split('/'))
        {
            var token = Unescape(raw);

            if (node.IsArray)
            {
                if (!int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                    || index >= node.Items!.Count)
                    return null;

                node = node.Items[index];
            }
            else if (node.IsObject && node.Get(token) is { } next)
            {
                node = next;
            }
            else
            {
                return null;
            }
        }

        return node;
    }

    private static string Unescape(string token)
    {
        if (token.IndexOf('%') >= 0)
        {
            var bytes = new List<byte>();

            for (var i = 0; i < token.Length; i++)
            {
                if (token[i] == '%' && i + 2 < token.Length
                    && byte.TryParse(token.Substring(i + 1, 2), System.Globalization.NumberStyles.AllowHexSpecifier,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    bytes.Add(value);
                    i += 2;
                }
                else
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(token[i].ToString()));
                }
            }

            token = Encoding.UTF8.GetString(bytes.ToArray());
        }

        return token.Replace("~1", "/").Replace("~0", "~");
    }

    /// <summary>The file name without the extensions ".schema.json" or ".json".</summary>
    public static string Stem(string key)
    {
        var name = key.Substring(key.LastIndexOf('/') + 1);

        foreach (var suffix in new[] { ".schema.json", "-schema.json", ".json" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
                return name.Substring(0, name.Length - suffix.Length);
        }

        return name;
    }
}

/// <summary>Finds referenced documents among a set of known files (the project's AdditionalFiles).</summary>
internal sealed class SiblingDocuments
{
    private readonly List<KeyValuePair<string, string>> _files = new();

    public SiblingDocuments(IEnumerable<KeyValuePair<string, string>> files)
    {
        foreach (var file in files)
            _files.Add(new KeyValuePair<string, string>(DocumentPaths.Normalize(file.Key), file.Value));
    }

    /// <summary>
    /// Matches on the exact combined path first, then on a unique path ending in the reference, then on a unique file name.
    /// </summary>
    public string? Load(string key, string reference)
    {
        foreach (var file in _files)
        {
            if (string.Equals(file.Key, key, StringComparison.OrdinalIgnoreCase))
                return file.Value;
        }

        var relative = DocumentPaths.Normalize(reference.Replace('\\', '/')).TrimStart('/');
        var byPath = Unique(f => f.Key.EndsWith("/" + relative, StringComparison.OrdinalIgnoreCase) || f.Key.Equals(relative, StringComparison.OrdinalIgnoreCase));

        if (byPath is not null)
            return byPath;

        var name = relative.Substring(relative.LastIndexOf('/') + 1);
        return Unique(f => f.Key.Substring(f.Key.LastIndexOf('/') + 1).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private string? Unique(Func<KeyValuePair<string, string>, bool> match)
    {
        string? found = null;

        foreach (var file in _files)
        {
            if (!match(file))
                continue;

            if (found is not null)
                return null; // ambiguous

            found = file.Value;
        }

        return found;
    }
}
