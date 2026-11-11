using System.Text.Json;
using JsonCall.Core.Model;

namespace JsonCall.Core.Protocol;

/// <summary>Handles a request. Returns null for notifications.</summary>
public interface IJsonRpcHandler
{
    ValueTask<JsonRpcReply?> HandleAsync(JsonRpcRequest request, JsonRpcCallContext context);
}

/// <summary>The outcome of handling a request.</summary>
public sealed class JsonRpcReply
{
    private JsonRpcReply()
    {
    }

    public JsonElement? Result { get; private init; }

    public JsonRpcError? Error { get; private init; }

    public IAsyncEnumerable<JsonElement>? Items { get; private init; }

    /// <summary>Maps an exception thrown while enumerating a stream to an error.</summary>
    public Func<Exception, JsonRpcError>? MapError { get; private init; }

    public bool IsStream => Items is not null;

    public static JsonRpcReply FromResult(JsonElement result) => new() { Result = result };

    public static JsonRpcReply FromError(JsonRpcError error) => new() { Error = error };

    public static JsonRpcReply FromError(int code, string message) => new() { Error = new JsonRpcError(code, message) };

    public static JsonRpcReply FromStream(IAsyncEnumerable<JsonElement> items, Func<Exception, JsonRpcError> mapError) =>
        new() { Items = items, MapError = mapError };
}

/// <summary>Per-call state. Cleanup callbacks run when the call, or its stream, has finished.</summary>
public sealed class JsonRpcCallContext : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly List<Func<ValueTask>> _cleanup = new();

    public JsonRpcCallContext(JsonRpcRequest request, CancellationToken parent = default)
    {
        Request = request;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
    }

    public JsonRpcRequest Request { get; }

    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>Scoped services for the call. Null when the host has not provided any.</summary>
    public IServiceProvider? Services { get; set; }

    /// <summary>Whether a streaming method may be invoked (single call on a transport that can stream).</summary>
    public bool StreamsAllowed { get; set; } = true;

    public IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();

    public void Cancel() => _cts.Cancel();

    public void OnCompleted(Func<ValueTask> callback)
    {
        lock (_cleanup)
            _cleanup.Add(callback);
    }

    public async ValueTask CompleteAsync()
    {
        Func<ValueTask>[] callbacks;

        lock (_cleanup)
        {
            callbacks = _cleanup.ToArray();
            _cleanup.Clear();
        }

        foreach (var callback in callbacks)
        {
            try
            {
                await callback().ConfigureAwait(false);
            }
            catch
            {
                // cleanup must not fail the call
            }
        }

        _cts.Dispose();
    }

    public void Dispose() => _cts.Cancel();
}

/// <summary>A streamed reply, produced for a single (non batch) call.</summary>
public sealed class StreamingResult
{
    public required JsonRpcId Id { get; init; }

    public required IAsyncEnumerable<JsonElement> Items { get; init; }

    public required Func<Exception, JsonRpcError> MapError { get; init; }

    public required JsonRpcCallContext Context { get; init; }
}

public sealed class ProcessResult
{
    /// <summary>Canonical JSON of the response (object or array), or null when nothing is to be sent.</summary>
    public byte[]? ResponseJson { get; init; }

    public StreamingResult? Stream { get; init; }

    public static ProcessResult None { get; } = new();
}

/// <summary>Turns a parsed message into responses, handling batches and invalid items per the specification.</summary>
public static class JsonRpcRequestProcessor
{
    public static async ValueTask<ProcessResult> ProcessAsync(
        JsonElement root,
        IJsonRpcHandler handler,
        Func<JsonRpcRequest, JsonRpcCallContext> contextFactory,
        bool allowStream = true)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0)
            {
                return new ProcessResult
                {
                    ResponseJson = JsonRpcWire.SerializeResponse(JsonRpcResponse.Failure(JsonRpcId.Null,
                        JsonRpcErrorCodes.InvalidRequest, "Invalid Request: empty batch"))
                };
            }

            var tasks = new List<Task<JsonRpcResponse?>>();

            foreach (var element in root.EnumerateArray())
                tasks.Add(HandleItemAsync(element, handler, contextFactory, allowStream: false).AsTask()
                    .ContinueWith(t => t.Result.Response, TaskScheduler.Default));

            var responses = (await Task.WhenAll(tasks).ConfigureAwait(false)).Where(r => r is not null).Select(r => r!).ToList();

            return responses.Count == 0
                ? ProcessResult.None
                : new ProcessResult { ResponseJson = JsonRpcWire.SerializeResponses(responses) };
        }

        var (response, stream) = await HandleItemAsync(root, handler, contextFactory, allowStream).ConfigureAwait(false);

        if (stream is not null)
            return new ProcessResult { Stream = stream };

        return response is null
            ? ProcessResult.None
            : new ProcessResult { ResponseJson = JsonRpcWire.SerializeResponse(response) };
    }

    private static async ValueTask<(JsonRpcResponse? Response, StreamingResult? Stream)> HandleItemAsync(
        JsonElement element,
        IJsonRpcHandler handler,
        Func<JsonRpcRequest, JsonRpcCallContext> contextFactory,
        bool allowStream)
    {
        var item = IncomingItem.Parse(element);

        switch (item)
        {
            case IncomingItem.Invalid invalid:
                return (JsonRpcResponse.Failure(invalid.Id, invalid.Code, invalid.Message), null);

            case IncomingItem.Response:
                return (null, null);
        }

        var request = ((IncomingItem.Request)item).Value;
        var context = contextFactory(request);
        context.StreamsAllowed = allowStream;

        JsonRpcReply? reply;

        try
        {
            reply = await handler.HandleAsync(request, context).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await context.CompleteAsync().ConfigureAwait(false);

            return request.IsNotification
                ? (null, null)
                : (JsonRpcResponse.Failure(request.Id!.Value, JsonRpcErrorCodes.InternalError, "Internal error"), null);
        }

        if (request.IsNotification || reply is null)
        {
            await context.CompleteAsync().ConfigureAwait(false);
            return (null, null);
        }

        var id = request.Id!.Value;

        if (reply.IsStream)
        {
            if (allowStream)
            {
                return (null, new StreamingResult
                {
                    Id = id,
                    Items = reply.Items!,
                    MapError = reply.MapError!,
                    Context = context
                });
            }

            await context.CompleteAsync().ConfigureAwait(false);

            return (JsonRpcResponse.Failure(id, JsonRpcErrorCodes.StreamRequired,
                "The method streams its result and cannot be called in a batch."), null);
        }

        await context.CompleteAsync().ConfigureAwait(false);

        return (reply.Error is { } error
            ? JsonRpcResponse.Failure(id, error)
            : JsonRpcResponse.Success(id, reply.Result ?? default), null);
    }
}
