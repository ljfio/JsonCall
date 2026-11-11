using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using JsonCall.Core.Model;

namespace JsonCall.Core.Protocol;

public sealed class JsonRpcPeerOptions
{
    public JsonSerializerOptions SerializerOptions { get; set; } = JsonRpcSerializer.DefaultOptions;

    public IMessageSerializer Serializer { get; set; } = JsonMessageSerializer.Instance;

    /// <summary>Default timeout for calls. <see cref="Timeout.InfiniteTimeSpan"/> disables it.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Items buffered per client-side stream before the read loop applies back-pressure.</summary>
    public int StreamBufferSize { get; set; } = 64;

    /// <summary>Handler for incoming requests (server-to-client calls, or the server side of a connection).</summary>
    public IJsonRpcHandler? Handler { get; set; }

    /// <summary>Items copied to each call context (for example the HttpContext of a WebSocket connection).</summary>
    public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    public Action<Exception>? OnError { get; set; }
}

public sealed class BatchItem
{
    public BatchItem(string method, JsonRpcParams? @params = null, bool isNotification = false)
    {
        Method = method;
        Params = @params;
        IsNotification = isNotification;
    }

    public string Method { get; }

    public JsonRpcParams? Params { get; }

    public bool IsNotification { get; }
}

public sealed class BatchResult
{
    internal BatchResult(JsonElement? result, JsonRpcException? error, JsonSerializerOptions options)
    {
        Result = result;
        Error = error;
        Options = options;
    }

    private JsonSerializerOptions Options { get; }

    public JsonElement? Result { get; }

    public JsonRpcException? Error { get; }

    public bool Succeeded => Error is null;

    public T? GetResult<T>()
    {
        if (Error is not null)
            throw Error;

        return Result is { } r ? r.Deserialize<T>(Options) : default;
    }
}

/// <summary>
/// One end of a JSON-RPC connection. Correlates calls to responses, delivers notifications, serves incoming
/// requests through an optional handler and implements the stream convention on message transports.
/// </summary>
public class JsonRpcPeer : IAsyncDisposable
{
    private abstract record StreamEvent
    {
        public sealed record Item(JsonElement Value) : StreamEvent;

        public sealed record End : StreamEvent;

        public sealed record Error(JsonRpcError Value) : StreamEvent;
    }

    private sealed class PendingCall
    {
        public PendingCall(bool expectsStream) => ExpectsStream = expectsStream;

        public bool ExpectsStream { get; }

        public TaskCompletionSource<JsonRpcResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? StreamId { get; set; }

        public Channel<StreamEvent>? Stream { get; set; }
    }

    private readonly IMessageTransport _transport;
    private readonly JsonRpcPeerOptions _options;
    private readonly ConcurrentDictionary<JsonRpcId, PendingCall> _pending = new();
    private readonly ConcurrentDictionary<string, Channel<StreamEvent>> _streams = new();
    private readonly ConcurrentDictionary<JsonRpcId, JsonRpcCallContext> _inflight = new();
    private readonly ConcurrentDictionary<string, JsonRpcCallContext> _serverStreams = new();
    private readonly ConcurrentDictionary<string, Action<JsonElement?>> _notificationHandlers = new();
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private long _nextStream;
    private int _disposed;
    private Task? _readLoop;

    public JsonRpcPeer(IMessageTransport transport, JsonRpcPeerOptions? options = null)
    {
        _transport = transport;
        _options = options ?? new JsonRpcPeerOptions();
    }

    public JsonSerializerOptions SerializerOptions => _options.SerializerOptions;

    /// <summary>Default timeout for calls that do not specify one.</summary>
    public TimeSpan DefaultTimeout
    {
        get => _options.DefaultTimeout;
        set => _options.DefaultTimeout = value;
    }

    /// <summary>Completes when the read loop ends (peer closed, transport failed, or disposed).</summary>
    public Task Completion => _completion.Task;

    /// <summary>Raised for messages the peer could not process, for diagnostics.</summary>
    public IDictionary<object, object?> Items => _options.Items;

    public void Start()
    {
        if (_readLoop is not null)
            return;

        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Registers a handler for notifications of a given method sent by the other side.</summary>
    public void OnNotification(string method, Action<JsonElement?> handler) => _notificationHandlers[method] = handler;

    public void OnNotification<T>(string method, Action<T?> handler) =>
        OnNotification(method, e => handler(e is { } v ? v.Deserialize<T>(SerializerOptions) : default));

    // ---------------------------------------------------------------- outgoing

    public async ValueTask<JsonElement> InvokeAsync(string method, JsonRpcParams? @params = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var (response, _) = await InvokeCoreAsync(method, @params, timeout, cancellationToken, expectsStream: false)
            .ConfigureAwait(false);

        return response.Result ?? default;
    }

    public async ValueTask<T?> InvokeAsync<T>(string method, JsonRpcParams? @params = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(method, @params, timeout, cancellationToken).ConfigureAwait(false);

        return result.ValueKind == JsonValueKind.Undefined ? default : result.Deserialize<T>(SerializerOptions);
    }

    public async ValueTask NotifyAsync(string method, JsonRpcParams? @params = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var bytes = JsonRpcWire.Write(w => JsonRpcWire.WriteRequest(w, method, null, @params, SerializerOptions));

        await SendJsonAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Calls a streaming method and yields its items as they arrive.</summary>
    public async IAsyncEnumerable<T> StreamAsync<T>(string method, JsonRpcParams? @params = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (_, pending) = await InvokeCoreAsync(method, @params, null, cancellationToken, expectsStream: true)
            .ConfigureAwait(false);

        var channel = pending.Stream ?? throw new InvalidOperationException(
            $"The method '{method}' did not return a stream. Call it with InvokeAsync instead.");

        var finished = false;

        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (ev is StreamEvent.End)
                {
                    finished = true;
                    yield break;
                }

                if (ev is StreamEvent.Error error)
                {
                    finished = true;
                    throw new JsonRpcException(error.Value.Code, error.Value.Message, error.Value.Data);
                }

                yield return ((StreamEvent.Item)ev).Value.Deserialize<T>(SerializerOptions)!;
            }

            finished = true;
        }
        finally
        {
            if (pending.StreamId is { } streamId)
            {
                _streams.TryRemove(streamId, out _);
                channel.Writer.TryComplete();

                if (!finished && Volatile.Read(ref _disposed) == 0)
                    _ = SendStreamCancelAsync(streamId);
            }
        }
    }

    public async Task<IReadOnlyList<BatchResult>> BatchAsync(IReadOnlyList<BatchItem> items,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var calls = new List<(PendingCall Pending, JsonRpcId Id)>();

        var bytes = JsonRpcWire.Write(w =>
        {
            w.WriteStartArray();

            foreach (var item in items)
            {
                JsonRpcId? id = null;

                if (!item.IsNotification)
                {
                    var newId = JsonRpcId.FromNumber(Interlocked.Increment(ref _nextId));
                    var pending = new PendingCall(false);
                    _pending[newId] = pending;
                    calls.Add((pending, newId));
                    id = newId;
                }

                JsonRpcWire.WriteRequest(w, item.Method, id, item.Params, SerializerOptions);
            }

            w.WriteEndArray();
        });

        try
        {
            ThrowIfConnectionClosed(calls.Select(c => c.Id).ToArray());

            await SendJsonAsync(bytes, cancellationToken).ConfigureAwait(false);

            if (calls.Count == 0)
                return Array.Empty<BatchResult>();

            using var timeoutCts = new CancellationTokenSource(timeout ?? _options.DefaultTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var results = new List<BatchResult>();

            foreach (var (pending, _) in calls)
            {
                JsonRpcResponse response;

                try
                {
                    response = await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                        && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The batch call timed out.");
                }

                results.Add(response.Error is { } e
                    ? new BatchResult(null, new JsonRpcException(e.Code, e.Message, e.Data), SerializerOptions)
                    : new BatchResult(response.Result, null, SerializerOptions));
            }

            return results;
        }
        finally
        {
            foreach (var (_, id) in calls)
                _pending.TryRemove(id, out _);
        }
    }

    private async Task<(JsonRpcResponse Response, PendingCall Pending)> InvokeCoreAsync(string method,
        JsonRpcParams? @params, TimeSpan? timeout, CancellationToken cancellationToken, bool expectsStream)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var id = JsonRpcId.FromNumber(Interlocked.Increment(ref _nextId));
        var pending = new PendingCall(expectsStream);
        _pending[id] = pending;
        ThrowIfConnectionClosed(id);

        using var timeoutCts = new CancellationTokenSource(timeout ?? _options.DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var bytes = JsonRpcWire.Write(w => JsonRpcWire.WriteRequest(w, method, id, @params, SerializerOptions));

            await SendJsonAsync(bytes, linked.Token).ConfigureAwait(false);

            var response = await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);

            if (response.Error is { } error)
                throw new JsonRpcException(error.Code, error.Message, error.Data);

            return (response, pending);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                && !cancellationToken.IsCancellationRequested)
        {
            _ = SendCancelAsync(id);
            throw new TimeoutException($"The call to '{method}' timed out.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = SendCancelAsync(id);
            throw;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendCancelAsync(JsonRpcId id)
    {
        try
        {
            var bytes = JsonRpcWire.Write(w => JsonRpcWire.WriteRequestNode(w, "$/cancel", null,
                new JsonObject { ["id"] = JsonNode.Parse(id.RawJson) }, SerializerOptions));

            await SendJsonAsync(bytes, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }
    }

    private async Task SendStreamCancelAsync(string streamId)
    {
        try
        {
            var bytes = JsonRpcWire.Write(w => JsonRpcWire.WriteRequestNode(w, "$/cancel", null,
                new JsonObject { ["stream"] = streamId }, SerializerOptions));

            await SendJsonAsync(bytes, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }
    }

    private async ValueTask SendJsonAsync(byte[] json, CancellationToken cancellationToken)
    {
        var wire = _options.Serializer.FromJson(json);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _transport.SendAsync(wire, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ---------------------------------------------------------------- incoming

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;

        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var message = await _transport.ReceiveAsync(_lifetime.Token).ConfigureAwait(false);

                if (message is null)
                    break;

                await HandleIncomingAsync(message).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            Shutdown(failure);
        }
    }

    private async ValueTask HandleIncomingAsync(byte[] wire)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(_options.Serializer.ToJson(wire));
        }
        catch (Exception ex) when (ex is JsonException or JsonRpcParseException or ArgumentException)
        {
            if (_options.Handler is not null)
            {
                var error = JsonRpcWire.SerializeResponse(JsonRpcResponse.Failure(JsonRpcId.Null,
                    JsonRpcErrorCodes.ParseError, "Parse error"));

                TrackBackground(SendQuietlyAsync(error));
            }

            return;
        }

        var root = document.RootElement;
        var keep = false;

        try
        {
            if (root.ValueKind == JsonValueKind.Object && IncomingItem.LooksLikeResponse(root))
            {
                CompleteResponse(root);
            }
            else if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                                                            && IncomingItem.LooksLikeResponse(root[0]))
            {
                foreach (var element in root.EnumerateArray())
                    if (IncomingItem.LooksLikeResponse(element))
                        CompleteResponse(element);
            }
            else if (root.ValueKind == JsonValueKind.Object && await TryHandleInternalAsync(root).ConfigureAwait(false))
            {
            }
            else
            {
                keep = true;
                TrackBackground(ProcessRequestsAsync(document));
            }
        }
        finally
        {
            if (!keep)
                document.Dispose();
        }
    }

    private void CompleteResponse(JsonElement element)
    {
        if (IncomingItem.Parse(element) is not IncomingItem.Response { Value: var response })
            return;

        if (!_pending.TryGetValue(response.Id, out var pending))
            return; // late response after timeout or cancellation

        if (pending.ExpectsStream && response.Error is null
            && response.Result is { ValueKind: JsonValueKind.Object } result
            && result.TryGetProperty("stream", out var streamProperty)
            && streamProperty.ValueKind == JsonValueKind.String)
        {
            var streamId = streamProperty.GetString()!;
            var channel = Channel.CreateBounded<StreamEvent>(new BoundedChannelOptions(_options.StreamBufferSize)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            pending.StreamId = streamId;
            pending.Stream = channel;
            _streams[streamId] = channel;
        }

        pending.Completion.TrySetResult(response);
    }

    /// <summary>Handles $/stream.* and $/cancel inline so that ordering and back-pressure are preserved.</summary>
    private async ValueTask<bool> TryHandleInternalAsync(JsonElement root)
    {
        if (!root.TryGetProperty("method", out var methodElement)
            || methodElement.ValueKind != JsonValueKind.String
            || methodElement.GetString() is not { } method
            || !method.StartsWith("$/", StringComparison.Ordinal)
            || root.TryGetProperty("id", out _))
        {
            return false;
        }

        root.TryGetProperty("params", out var p);

        if (p.ValueKind != JsonValueKind.Object)
            return true;

        switch (method)
        {
            case "$/stream.item" or "$/stream.end" or "$/stream.error":
            {
                if (!p.TryGetProperty("stream", out var s) || s.GetString() is not { } streamId
                    || !_streams.TryGetValue(streamId, out var channel))
                    return true;

                StreamEvent ev = method switch
                {
                    "$/stream.item" => new StreamEvent.Item(p.TryGetProperty("value", out var v) ? v.Clone() : default),
                    "$/stream.end" => new StreamEvent.End(),
                    _ => new StreamEvent.Error(p.TryGetProperty("error", out var e)
                        ? JsonRpcWire.ReadError(e)
                        : new JsonRpcError(JsonRpcErrorCodes.InternalError, "Stream failed"))
                };

                try
                {
                    await channel.Writer.WriteAsync(ev, _lifetime.Token).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    // consumer abandoned the stream
                }

                if (ev is not StreamEvent.Item)
                    channel.Writer.TryComplete();

                return true;
            }

            case "$/cancel":
            {
                if (p.TryGetProperty("id", out var idElement) && JsonRpcId.TryFrom(idElement, out var id)
                    && _inflight.TryGetValue(id, out var call))
                {
                    call.Cancel();
                }
                else if (p.TryGetProperty("stream", out var s) && s.GetString() is { } streamId
                         && _serverStreams.TryGetValue(streamId, out var streamCall))
                {
                    streamCall.Cancel();
                }

                return true;
            }
        }

        return false;
    }

    private async Task ProcessRequestsAsync(JsonDocument document)
    {
        try
        {
            var result = await JsonRpcRequestProcessor.ProcessAsync(
                document.RootElement, new PeerHandler(this), CreateContext).ConfigureAwait(false);

            if (result.ResponseJson is { } json)
                await SendJsonAsync(json, _lifetime.Token).ConfigureAwait(false);

            if (result.Stream is { } stream)
                await PumpStreamAsync(stream).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
        catch (Exception ex)
        {
            _options.OnError?.Invoke(ex);
        }
        finally
        {
            document.Dispose();
        }
    }

    private JsonRpcCallContext CreateContext(JsonRpcRequest request)
    {
        var context = new JsonRpcCallContext(request, _lifetime.Token)
        {
            Items = new Dictionary<object, object?>(_options.Items)
        };

        if (request.Id is { } id)
        {
            _inflight[id] = context;
            context.OnCompleted(() =>
            {
                _inflight.TryRemove(id, out _);
                return ValueTask.CompletedTask;
            });
        }

        return context;
    }

    private async Task PumpStreamAsync(StreamingResult stream)
    {
        var streamId = "s" + Interlocked.Increment(ref _nextStream);
        var context = stream.Context;
        _serverStreams[streamId] = context;

        try
        {
            var response = JsonRpcWire.SerializeResponse(JsonRpcResponse.Success(stream.Id,
                JsonSerializer.SerializeToElement(new { stream = streamId })));

            await SendJsonAsync(response, _lifetime.Token).ConfigureAwait(false);

            long sequence = 0;

            try
            {
                await foreach (var item in stream.Items.WithCancellation(context.CancellationToken).ConfigureAwait(false))
                {
                    var seq = sequence++;

                    var bytes = JsonRpcWire.Write(w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("jsonrpc", "2.0");
                        w.WriteString("method", "$/stream.item");
                        w.WritePropertyName("params");
                        w.WriteStartObject();
                        w.WriteString("stream", streamId);
                        w.WriteNumber("seq", seq);
                        w.WritePropertyName("value");
                        item.WriteTo(w);
                        w.WriteEndObject();
                        w.WriteEndObject();
                    });

                    await SendJsonAsync(bytes, _lifetime.Token).ConfigureAwait(false);
                }

                await SendStreamNotificationAsync("$/stream.end", streamId, null).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException && context.CancellationToken.IsCancellationRequested)
            {
                // cancelled by the other side or by shutdown: nothing more to send
            }
            catch (Exception ex) when (ex is not (IOException or ObjectDisposedException))
            {
                await SendStreamNotificationAsync("$/stream.error", streamId, stream.MapError(ex)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            _serverStreams.TryRemove(streamId, out _);
            await context.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async Task SendStreamNotificationAsync(string method, string streamId, JsonRpcError? error)
    {
        var bytes = JsonRpcWire.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteString("method", method);
            w.WritePropertyName("params");
            w.WriteStartObject();
            w.WriteString("stream", streamId);

            if (error is not null)
            {
                w.WritePropertyName("error");
                JsonRpcWire.WriteError(w, error);
            }

            w.WriteEndObject();
            w.WriteEndObject();
        });

        await SendJsonAsync(bytes, _lifetime.Token).ConfigureAwait(false);
    }

    private async Task SendQuietlyAsync(byte[] json)
    {
        try
        {
            await SendJsonAsync(json, _lifetime.Token).ConfigureAwait(false);
        }
        catch
        {
            // connection is going away
        }
    }

    private void TrackBackground(Task task)
    {
        _running[task] = 0;
        task.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
    }

    private void Shutdown(Exception? failure)
    {
        var exception = new ConnectionLostException(
            failure is null ? "The JSON-RPC connection was closed." : "The JSON-RPC connection was lost.", failure);

        foreach (var pending in _pending.Values)
            pending.Completion.TrySetException(exception);

        foreach (var stream in _streams.Values)
            stream.Writer.TryComplete(exception);

        foreach (var call in _inflight.Values)
            call.Cancel();

        foreach (var call in _serverStreams.Values)
            call.Cancel();

        _completion.TrySetResult();
    }

    // ---------------------------------------------------------------- handler glue

    private sealed class PeerHandler : IJsonRpcHandler
    {
        private readonly JsonRpcPeer _peer;

        public PeerHandler(JsonRpcPeer peer) => _peer = peer;

        public ValueTask<JsonRpcReply?> HandleAsync(JsonRpcRequest request, JsonRpcCallContext context)
        {
            if (request.IsNotification && _peer._notificationHandlers.TryGetValue(request.Method, out var handler))
            {
                handler(request.Params is { } p ? p.Clone() : null);
                return ValueTask.FromResult<JsonRpcReply?>(null);
            }

            if (_peer._options.Handler is { } user)
                return user.HandleAsync(request, context);

            return ValueTask.FromResult<JsonRpcReply?>(request.IsNotification
                ? null
                : JsonRpcReply.FromError(JsonRpcErrorCodes.MethodNotFound, "Method not found"));
        }
    }

    // ---------------------------------------------------------------- lifetime

    private void ThrowIfConnectionClosed(params JsonRpcId[] ids)
    {
        if (!_completion.Task.IsCompleted)
            return;

        foreach (var id in ids)
            _pending.TryRemove(id, out _);

        throw new ConnectionLostException("The JSON-RPC connection is closed.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetime.Cancel();

        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
        else
        {
            Shutdown(null);
        }

        try
        {
            await Task.WhenAll(_running.Keys).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        _lifetime.Dispose();
    }
}
