using System.Text.Json;
using JsonCall.Core.Model;

namespace JsonCall.OpenRpc;

/// <summary>Settings for OpenRPC document generation and the <c>rpc.discover</c> module.</summary>
public sealed class OpenRpcOptions
{
    /// <summary>The document's <c>info.title</c>. Defaults to the contract's title, then to the assembly name.</summary>
    public string? Title { get; set; }

    /// <summary>The document's <c>info.version</c>. Defaults to the contract's version, then "1.0.0".</summary>
    public string? Version { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// The public URL of the endpoint. When set, the document lists it under <c>servers</c>.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Controls property names in schemas. Defaults to the serializer options of the server,
    /// or to <see cref="JsonRpcSerializer.DefaultOptions"/> when generating without a server.
    /// </summary>
    public JsonSerializerOptions? SerializerOptions { get; set; }

    /// <summary>
    /// Metadata applied to <c>rpc.discover</c>, for example an <c>AuthorizeAttribute</c> to protect discovery.
    /// </summary>
    public IList<object> Metadata { get; } = new List<object>();
}
