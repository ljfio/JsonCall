using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using JsonCall.Core;
using JsonCall.Core.Model;
using JsonCall.Core.Protocol;

namespace JsonCall.Transport;

/// <summary>
/// Client transport over HTTP. Every message is a POST; the response body (or Server-Sent Events stream) becomes
/// incoming messages. SSE streams are translated into the same $/stream.* messages used on stateful transports,
/// so the client has a single streaming implementation.
/// </summary>
public sealed class HttpMessageTransport : IMessageTransport
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly IMessageSerializer _serializer;
    private readonly bool _ownsClient;
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _calls = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _streams = new();
    private readonly CancellationTokenSource _lifetime = new();
    private long _nextStream;

    public HttpMessageTransport(HttpClient http, Uri endpoint, IMessageSerializer? serializer = null, bool ownsClient = false)
    {
        _http = http;
        _endpoint = endpoint;
        _serializer = serializer ?? JsonMessageSerializer.Instance;
        _ownsClient = ownsClient;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        var wire = message.ToArray();
        string? idRaw = null;

        using (var document = JsonDocument.Parse(_serializer.ToJson(wire)))
        {
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("method", out var method) && method.GetString() == "$/cancel")
                {
                    Cancel(root);
                    return ValueTask.CompletedTask;
                }

                if (root.TryGetProperty("id", out var id))
                    idRaw = id.GetRawText();
            }
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

        if (idRaw is not null)
            _calls[idRaw] = cts;

        _ = Task.Run(() => PostAsync(wire, idRaw, cts));

        return ValueTask.CompletedTask;
    }

    private void Cancel(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object)
            return;

        if (p.TryGetProperty("stream", out var stream) && stream.GetString() is { } streamId
            && _streams.TryGetValue(streamId, out var streamCts))
        {
            streamCts.Cancel();
        }
        else if (p.TryGetProperty("id", out var id) && _calls.TryGetValue(id.GetRawText(), out var callCts))
        {
            callCts.Cancel();
        }
    }

    private async Task PostAsync(byte[] body, string? idRaw, CancellationTokenSource cts)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(_serializer.ContentType);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(_serializer.ContentType));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            var contentType = response.Content.Headers.ContentType?.MediaType;

            if (contentType == "text/event-stream")
            {
                await ReadEventStreamAsync(response, idRaw, cts.Token).ConfigureAwait(false);
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);

            if (bytes.Length > 0 && contentType == _serializer.ContentType)
            {
                await _incoming.Writer.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
            }
            else if (!response.IsSuccessStatusCode && idRaw is not null)
            {
                await EnqueueJsonAsync(JsonRpcWire.SerializeResponse(JsonRpcResponse.Failure(
                    ParseId(idRaw), JsonRpcErrorCodes.ServerError, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}")))
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled by the caller or disposal
        }
        catch (Exception ex) when (idRaw is not null)
        {
            await EnqueueJsonAsync(JsonRpcWire.SerializeResponse(JsonRpcResponse.Failure(
                ParseId(idRaw), JsonRpcErrorCodes.ServerError, "HTTP request failed: " + ex.Message))).ConfigureAwait(false);
        }
        catch
        {
            // a notification failed: nothing to report to
        }
        finally
        {
            if (idRaw is not null)
                _calls.TryRemove(idRaw, out _);

            cts.Dispose();
        }
    }

    private async Task ReadEventStreamAsync(HttpResponseMessage response, string? idRaw, CancellationToken token)
    {
        var streamId = "h" + Interlocked.Increment(ref _nextStream);

        // Cancelling the stream is cancelling this linked source (via $/cancel {stream}).
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _streams[streamId] = streamCts;

        try
        {
            if (idRaw is not null)
            {
                await EnqueueJsonAsync(JsonRpcWire.Write(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("jsonrpc", "2.0");
                    w.WritePropertyName("result");
                    w.WriteStartObject();
                    w.WriteString("stream", streamId);
                    w.WriteEndObject();
                    w.WritePropertyName("id");
                    w.WriteRawValue(idRaw);
                    w.WriteEndObject();
                })).ConfigureAwait(false);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(streamCts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? eventName = null;
            var data = new StringBuilder();
            var seq = 0L;

            while (true)
            {
                var line = await reader.ReadLineAsync(streamCts.Token).ConfigureAwait(false);

                if (line is null)
                {
                    await EnqueueStreamAsync("$/stream.error", streamId, null, new JsonRpcError(
                        JsonRpcErrorCodes.ServerError, "The event stream ended unexpectedly.")).ConfigureAwait(false);
                    return;
                }

                if (line.Length == 0)
                {
                    if (data.Length > 0 || eventName is not null)
                    {
                        var payload = data.ToString();

                        switch (eventName)
                        {
                            case "complete":
                                await EnqueueStreamAsync("$/stream.end", streamId, null, null).ConfigureAwait(false);
                                return;

                            case "error":
                            {
                                using var errorDocument = JsonDocument.Parse(payload);
                                await EnqueueStreamAsync("$/stream.error", streamId, null,
                                    JsonRpcWire.ReadError(errorDocument.RootElement)).ConfigureAwait(false);
                                return;
                            }

                            default:
                                await EnqueueStreamAsync("$/stream.item", streamId, (seq++, payload), null).ConfigureAwait(false);
                                break;
                        }
                    }

                    eventName = null;
                    data.Clear();
                    continue;
                }

                if (line[0] == ':')
                    continue;

                var colon = line.IndexOf(':');
                var field = colon < 0 ? line : line[..colon];
                var value = colon < 0 ? string.Empty : line[(colon + 1)..].TrimStart(' ');

                if (field == "event")
                {
                    eventName = value;
                }
                else if (field == "data")
                {
                    if (data.Length > 0)
                        data.Append('\n');

                    data.Append(value);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stream abandoned by the client
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            await EnqueueStreamAsync("$/stream.error", streamId, null,
                new JsonRpcError(JsonRpcErrorCodes.ServerError, "The event stream failed: " + ex.Message)).ConfigureAwait(false);
        }
        finally
        {
            _streams.TryRemove(streamId, out _);
        }
    }

    private Task EnqueueStreamAsync(string method, string streamId, (long Seq, string Json)? item, JsonRpcError? error) =>
        EnqueueJsonAsync(JsonRpcWire.Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteString("method", method);
            w.WritePropertyName("params");
            w.WriteStartObject();
            w.WriteString("stream", streamId);

            if (item is { } i)
            {
                w.WriteNumber("seq", i.Seq);
                w.WritePropertyName("value");
                w.WriteRawValue(i.Json);
            }

            if (error is not null)
            {
                w.WritePropertyName("error");
                JsonRpcWire.WriteError(w, error);
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }));

    private async Task EnqueueJsonAsync(byte[] json) =>
        await _incoming.Writer.WriteAsync(_serializer.FromJson(json)).ConfigureAwait(false);

    private static JsonRpcId ParseId(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return JsonRpcId.TryFrom(document.RootElement, out var id) ? id : JsonRpcId.Null;
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _incoming.Writer.TryComplete();

        if (_ownsClient)
            _http.Dispose();

        return ValueTask.CompletedTask;
    }
}
