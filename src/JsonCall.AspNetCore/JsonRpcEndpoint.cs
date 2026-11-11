using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Core.Protocol;
using JsonCall.Server;
using JsonCall.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;

namespace JsonCall.AspNetCore;

public static class JsonRpcHttp
{
    /// <summary>The key under which the current HttpContext is stored in <see cref="JsonRpcCallContext.Items"/>.</summary>
    public const string HttpContextKey = "JsonCall.HttpContext";
}

internal sealed class JsonRpcEndpoint
{
    private readonly JsonRpcServer _server;
    private readonly JsonRpcHostOptions _host;
    private readonly JsonRpcEndpointOptions _endpoint;
    private readonly IHostApplicationLifetime _lifetime;

    public JsonRpcEndpoint(JsonRpcServer server, JsonRpcHostOptions host, JsonRpcEndpointOptions endpoint,
        IHostApplicationLifetime lifetime)
    {
        _server = server;
        _host = host;
        _endpoint = endpoint;
        _lifetime = lifetime;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest)
            return HandleWebSocketAsync(context);

        if (HttpMethods.IsPost(context.Request.Method))
            return HandlePostAsync(context);

        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers.Allow = "POST";
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ WebSocket

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!_endpoint.Transports.HasFlag(JsonRpcTransports.WebSocket))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var serializer = JsonMessageSerializer.Instance as IMessageSerializer;
        string? subProtocol = null;

        foreach (var requested in context.WebSockets.WebSocketRequestedProtocols)
        {
            if (requested.StartsWith("jsoncall.", StringComparison.Ordinal)
                && _host.Serializers.TryGetValue(requested["jsoncall.".Length..], out var match))
            {
                serializer = match;
                subProtocol = requested;
                break;
            }
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(subProtocol).ConfigureAwait(false);

        var transport = new WebSocketMessageTransport(socket, serializer, _host.MaxWebSocketMessageSize);

        await using var peer = _server.Attach(transport, serializer,
            o => o.Items[JsonRpcHttp.HttpContextKey] = context);

        using var stopping = _lifetime.ApplicationStopping.Register(() => _ = peer.DisposeAsync().AsTask());

        await Task.WhenAny(peer.Completion, Task.Delay(Timeout.Infinite, context.RequestAborted)
            .ContinueWith(_ => { }, TaskScheduler.Default)).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ HTTP

    private async Task HandlePostAsync(HttpContext context)
    {
        if (!_endpoint.Transports.HasFlag(JsonRpcTransports.Http))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var mediaType = MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var parsed) ? parsed.MediaType.Value : null;
        var serializer = _host.FindByContentType(mediaType);

        if (serializer is null)
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        byte[] body;

        try
        {
            body = await ReadBodyAsync(context).ConfigureAwait(false);
        }
        catch (MessageTooLargeException)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(serializer.ToJson(body));
        }
        catch (Exception ex) when (ex is JsonException or JsonRpcParseException or ArgumentException)
        {
            await WriteJsonAsync(context, serializer, JsonRpcWire.SerializeResponse(
                JsonRpcResponse.Failure(JsonRpcId.Null, JsonRpcErrorCodes.ParseError, "Parse error"))).ConfigureAwait(false);
            return;
        }

        using (document)
        {
            var acceptsStream = context.Request.GetTypedHeaders().Accept
                .Any(a => a.MediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase));

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, _lifetime.ApplicationStopping);

            var result = await JsonRpcRequestProcessor.ProcessAsync(
                document.RootElement,
                _server,
                request =>
                {
                    var call = new JsonRpcCallContext(request, linked.Token) { Services = context.RequestServices };
                    call.Items[JsonRpcHttp.HttpContextKey] = context;
                    return call;
                },
                acceptsStream).ConfigureAwait(false);

            if (result.Stream is { } stream)
            {
                await WriteEventStreamAsync(context, stream, linked.Token).ConfigureAwait(false);
                return;
            }

            if (result.ResponseJson is null)
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            if (!acceptsStream && IsStreamingCall(document.RootElement))
                context.Response.StatusCode = StatusCodes.Status406NotAcceptable;

            await WriteJsonAsync(context, serializer, result.ResponseJson).ConfigureAwait(false);
        }
    }

    private bool IsStreamingCall(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("method", out var method)
        && method.ValueKind == JsonValueKind.String
        && _server.TryGetMethod(method.GetString()!, out var registered)
        && registered.IsStream;

    private async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength > _host.MaxRequestBodySize)
            throw new MessageTooLargeException(_host.MaxRequestBodySize);

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;

        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);

            if (buffer.Length > _host.MaxRequestBodySize)
                throw new MessageTooLargeException(_host.MaxRequestBodySize);
        }

        return buffer.ToArray();
    }

    private static async Task WriteJsonAsync(HttpContext context, IMessageSerializer serializer, byte[] json)
    {
        var wire = serializer.FromJson(json);
        context.Response.ContentType = serializer.ContentType;
        context.Response.ContentLength = wire.Length;
        await context.Response.Body.WriteAsync(wire, context.RequestAborted).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ SSE

    private async Task WriteEventStreamAsync(HttpContext context, StreamingResult stream, CancellationToken token)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);

            try
            {
                // The enumerator is disposed when this loop exits, however it exits (including a failed write).
                await foreach (var item in stream.Items.WithCancellation(token).ConfigureAwait(false))
                    await WriteEventAsync(context, null, item.GetRawText()).ConfigureAwait(false);

                await WriteEventAsync(context, "complete", "{}").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // client went away
            }
            catch (OperationCanceledException) when (_lifetime.ApplicationStopping.IsCancellationRequested)
            {
                await TryWriteErrorAsync(context, new JsonRpcError(JsonRpcErrorCodes.ShuttingDown,
                    "The server is shutting down.")).ConfigureAwait(false);
            }
            catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
            {
                await TryWriteErrorAsync(context, stream.MapError(ex)).ConfigureAwait(false);
            }
        }
        finally
        {
            await stream.Context.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static async Task TryWriteErrorAsync(HttpContext context, JsonRpcError error)
    {
        try
        {
            var json = Encoding.UTF8.GetString(JsonRpcWire.Write(w => JsonRpcWire.WriteError(w, error)));
            await WriteEventAsync(context, "error", json).ConfigureAwait(false);
        }
        catch
        {
            // the response is already broken
        }
    }

    private static async Task WriteEventAsync(HttpContext context, string? name, string data)
    {
        var text = (name is null ? string.Empty : $"event: {name}\n") + $"data: {data}\n\n";

        await context.Response.WriteAsync(text, context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }
}
