using System;
using System.Collections.Generic;

namespace JsonCall.Generator.Core;

internal sealed partial class ClientGenerator
{
    private const int MaxReferenceDepth = 64;

    /// <summary>A parsed document that other documents refer to.</summary>
    private sealed class RefDocument
    {
        public string Key = "";
        public JNode Root = null!;
    }

    private DocumentLoader? _loader;
    private readonly Dictionary<string, RefDocument?> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _externalCache = new(StringComparer.Ordinal);

    // Set while mapping inside a referenced document: diagnostics there point at the $ref in the main document.
    private RefDocument? _current;
    private JNode? _anchor;
    private string _anchorPath = "";

    /// <summary>The key of the document currently being mapped (the main document unless inside a referenced one).</summary>
    private string CurrentKey => _current?.Key ?? DocumentPaths.Normalize(_fileName);

    private JNode CurrentRoot => _current?.Root ?? _root;

    /// <summary>Is <paramref name="reference"/> a $ref into the document's own components? Only the main document has those.</summary>
    private bool IsOwnComponent(string reference) =>
        _current is null && reference.StartsWith("#/components/schemas/", StringComparison.Ordinal);

    /// <summary>
    /// Maps the target of a $ref that is not a component of the main document: a pointer into the current document
    /// (such as <c>#/definitions/x</c>) or a sibling file (such as <c>event.schema.json#/definitions/x</c>).
    /// Returns null when it cannot be resolved; the diagnostic has been reported by then.
    /// </summary>
    private string? MapExternalReference(JNode refNode, string reference, string path, string hint)
    {
        if (DocumentPaths.IsRemote(reference))
        {
            WarnUnresolved(refNode, $"The reference '{reference}' points at a remote document, which is not fetched.", path);
            return null;
        }

        DocumentPaths.SplitReference(reference, out var file, out var pointer);

        RefDocument document;

        if (file.Length == 0)
        {
            document = _current ?? new RefDocument { Key = CurrentKey, Root = _root };
        }
        else
        {
            var key = DocumentPaths.Combine(CurrentKey, file);

            if (LoadDocument(key, file, refNode, path) is not { } loaded)
                return null;

            document = loaded;
        }

        var cacheKey = document.Key + "#" + pointer;

        if (_externalCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (DocumentPaths.Follow(document.Root, pointer) is not { } target)
        {
            WarnUnresolved(refNode, $"The reference '{reference}' does not lead to anything in '{document.Key}'.", path);
            return null;
        }

        var declared = Unique(ExternalName(document, pointer, target, hint));

        // Provisional answer so that a cyclic reference finds the type being built.
        _externalCache[cacheKey] = declared;

        if (_depth >= MaxReferenceDepth)
        {
            WarnUnresolved(refNode, $"The reference '{reference}' is nested too deeply.", path);
            return "JsonElement";
        }

        var (previous, previousAnchor, previousPath) = (_current, _anchor, _anchorPath);
        var previousAnchorSet = previousAnchor is not null;

        var isMain = ReferenceEquals(document.Root, _root);
        _current = isMain ? null : document;

        if (!previousAnchorSet && !isMain)
        {
            _anchor = refNode;
            _anchorPath = path;
        }

        _depth++;

        string mapped;

        try
        {
            mapped = Map(target, document.Key + "#" + (pointer.Length == 0 ? "" : pointer), declared, declared);
        }
        finally
        {
            _depth--;
            _current = previous;
            _anchor = previousAnchor;
            _anchorPath = previousPath;
        }

        _externalCache[cacheKey] = mapped;
        return mapped;
    }

    private int _depth;

    private RefDocument? LoadDocument(string key, string reference, JNode refNode, string path)
    {
        if (_documents.TryGetValue(key, out var existing))
        {
            if (existing is null)
                WarnUnresolved(refNode, $"The document '{reference}' could not be loaded.", path);

            return existing;
        }

        var text = _loader?.Invoke(key, reference);

        if (text is null)
        {
            _documents[key] = null;

            WarnUnresolved(refNode, _loader is null
                ? $"The reference '{reference}' points at another document, but no documents are available to resolve it."
                : $"The document '{reference}' was not found (looked for '{key}').", path);

            return null;
        }

        RefDocument? document = null;

        try
        {
            document = new RefDocument { Key = key, Root = JsonParser.Parse(text) };
        }
        catch (JsonParseException ex)
        {
            WarnUnresolved(refNode, $"The document '{reference}' is not valid JSON: {ex.Message}", path);
        }

        _documents[key] = document;

        if (document is not null)
        {
            _result.LoadedDocuments.Add(key);
            _result.Diagnostics.Add(new GenDiagnostic(DiagnosticIds.DocumentLoaded, GenSeverity.Info,
                $"Loaded the referenced document '{reference}'.", path, refNode));
        }

        return document;
    }

    private void WarnUnresolved(JNode refNode, string message, string path) =>
        Warn(DiagnosticIds.UnresolvedReference, GenSeverity.Warning, message + " The value is generated as JsonElement.",
            path + ".$ref", refNode.Get("$ref") ?? refNode);

    /// <summary>
    /// Names the type for a referenced schema: the file name for a whole document (a title that extends it, such as
    /// "Daemon event" for event.schema.json, wins), or the last pointer segment for a part of one.
    /// </summary>
    private static string ExternalName(RefDocument document, string pointer, JNode target, string hint)
    {
        if (pointer.Length == 0)
        {
            var stem = Naming.Pascal(DocumentPaths.Stem(document.Key));
            var title = target.GetString("title");

            if (!string.IsNullOrWhiteSpace(title))
            {
                var fromTitle = Naming.Pascal(title!);

                if (fromTitle.Length > stem.Length && fromTitle.EndsWith(stem, StringComparison.Ordinal))
                    return fromTitle;
            }

            return stem.Length > 0 ? stem : hint;
        }

        var last = pointer.Substring(pointer.LastIndexOf('/') + 1);
        return last.Length > 0 && !int.TryParse(last, out _) ? last : hint;
    }

    /// <summary>
    /// Follows a $ref (component or external) to the schema node it names, without mapping it. When
    /// <paramref name="refNode"/> is given, a document that is not loaded yet is loaded and failures are reported.
    /// </summary>
    private JNode? ResolveReferenceNode(string reference, out RefDocument? document, JNode? refNode = null, string path = "")
    {
        document = null;

        if (IsOwnComponent(reference))
            return _schemas?.Get(reference.Substring("#/components/schemas/".Length));

        if (DocumentPaths.IsRemote(reference))
        {
            if (refNode is not null)
                WarnUnresolved(refNode, $"The reference '{reference}' points at a remote document, which is not fetched.", path);

            return null;
        }

        DocumentPaths.SplitReference(reference, out var file, out var pointer);

        if (file.Length == 0)
        {
            document = _current;
            return DocumentPaths.Follow(CurrentRoot, pointer);
        }

        var key = DocumentPaths.Combine(CurrentKey, file);

        if (!_documents.TryGetValue(key, out document) && refNode is not null)
            document = LoadDocument(key, file, refNode, path);

        if (document is null)
            return null;

        var target = DocumentPaths.Follow(document.Root, pointer);

        if (target is null && refNode is not null)
            WarnUnresolved(refNode, $"The reference '{reference}' does not lead to anything in '{document.Key}'.", path);

        return target;
    }
}
