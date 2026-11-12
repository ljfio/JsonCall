---
type: Guide
title: Streaming
description: "IAsyncEnumerable<T> end to end, and the wire convention."
tags: [guide, streaming]
status: draft
---

# Streaming

A contract method that returns `IAsyncEnumerable<T>` is a stream. Items reach the caller as they are produced, on
every transport.

> **Draft.** JSON-RPC 2.0 does not define streaming, so this is a JsonCall extension. The convention below is
> implemented and tested, but it is not ratified ([decision 0002](../decisions/0002-stream-wire-convention.md)) and may change before 1.0. It is
> written down here so that clients in other languages can implement it and so that changes to it are deliberate.

## Using it

```csharp
[JsonRpcContract]
public interface IJobs
{
    IAsyncEnumerable<Progress> RunAsync(string jobId, CancellationToken ct = default);
}

public sealed class Jobs : IJobs
{
    public async IAsyncEnumerable<Progress> RunAsync(string jobId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        try
        {
            await foreach (var step in Execute(jobId, ct))
                yield return step;
        }
        finally
        {
            // Runs when the stream ends for any reason: completion, error, caller cancellation, disconnect.
        }
    }
}
```

```csharp
await foreach (var progress in client.CreateProxy<IJobs>().RunAsync("j-1", ct))
    Console.WriteLine(progress.Percent);
```

Without a contract: `client.StreamAsync<Progress>("run", JsonRpcParams.ByPosition("j-1"))`.

What you can rely on:

- **Not buffered.** Each item is sent when the producer yields it.
- **Cleanup always runs.** If the caller stops enumerating, cancels, or disconnects, the server cancels the method's
  token and disposes the enumerator, so `finally` blocks and `await using` run.
- **Errors end the stream.** An exception part-way through reaches the caller as a `JsonRpcException` from
  `MoveNextAsync`. Items already delivered stay delivered.
- **Back-pressure.** The client buffers up to `JsonRpcPeerOptions.StreamBufferSize` items (64 by default) per stream.

### Things to know

- **Dispose what you start.** On a stateful transport, a stream you stop reading without disposing its enumerator
  fills its buffer and then stalls *every* message on that connection, because back-pressure is applied on the
  connection's read loop. `await foreach` disposes for you, including on `break` and on exceptions. If you call
  `GetAsyncEnumerator` by hand, dispose it.
- **The call timeout covers only the start of a stream**, not its lifetime. Use a `CancellationToken` to bound a
  stream.
- **Streams cannot be batched.** A streaming method inside a batch returns error `-32001`.

## Wire convention: stateful transports

WebSocket, TCP, Unix sockets and stdio. Stream messages are ordinary JSON-RPC notifications whose method names begin
`$/`, a prefix JsonCall treats as internal.

**1. The caller sends a normal request.**

```json
{"jsonrpc": "2.0", "method": "run", "params": ["j-1"], "id": 7}
```

**2. The server answers with a stream handle** instead of a value. The id is unique per connection.

```json
{"jsonrpc": "2.0", "result": {"stream": "s1"}, "id": 7}
```

**3. The server sends items**, in order. `seq` starts at 0 and increases by one.

```json
{"jsonrpc": "2.0", "method": "$/stream.item", "params": {"stream": "s1", "seq": 0, "value": {"percent": 10}}}
```

**4. The server ends the stream**, exactly once, with either:

```json
{"jsonrpc": "2.0", "method": "$/stream.end", "params": {"stream": "s1"}}
```

```json
{"jsonrpc": "2.0", "method": "$/stream.error", "params": {"stream": "s1", "error": {"code": 4001, "message": "Job failed"}}}
```

`error` is a JSON-RPC error object.

**5. The caller may stop the stream early.** The server then stops producing and sends nothing further for it.

```json
{"jsonrpc": "2.0", "method": "$/cancel", "params": {"stream": "s1"}}
```

`$/cancel` with an `id` instead of a `stream` cancels an ordinary call that is still in flight:

```json
{"jsonrpc": "2.0", "method": "$/cancel", "params": {"id": 7}}
```

A cancelled call is answered with error `-32800`, or with its result if it had already finished.

Rules for implementers:

- A caller that did not ask for a stream and receives `{"stream": "..."}` simply sees that object as the result.
  There is no negotiation; the caller knows from the contract or from `x-jsoncall-stream` that the method streams.
- Messages for an unknown stream id are ignored.
- If the connection closes, every open stream fails on the caller and is cancelled on the server.

## Wire convention: HTTP

A POST whose `Accept` header includes `text/event-stream` may be answered with Server-Sent Events.

```http
POST /rpc
Content-Type: application/json
Accept: application/json, text/event-stream

{"jsonrpc": "2.0", "method": "run", "params": ["j-1"], "id": 7}
```

```http
HTTP/1.1 200 OK
Content-Type: text/event-stream

data: {"percent": 10}

data: {"percent": 60}

event: complete
data: {}

```

| Event | `data` | Meaning |
| --- | --- | --- |
| (unnamed) | The item, as JSON | One item. |
| `complete` | `{}` | The stream finished. Last event. |
| `error` | A JSON-RPC error object | The stream failed. Last event. |

- The caller cancels by closing the connection.
- A response that ends without `complete` or `error` is a failure.
- If the method fails before it starts streaming (unknown method, bad params, not authorized), the response is an
  ordinary `application/json` error response, not an event stream.
- Calling a streaming method without `Accept: text/event-stream` returns `406` with error `-32001` in the body.
- On server shutdown, open streams end with `error` code `-32005`.

`HttpMessageTransport` translates these events into the `$/stream.*` messages above, so a JsonCall client behaves
identically on every transport.

## In OpenRPC

A streaming method is marked `"x-jsoncall-stream": true`, and its `result` schema describes one item, not the
sequence. The client generator turns such a method into one that returns `IAsyncEnumerable<T>`.

## Error codes used by the convention

| Code | Meaning |
| --- | --- |
| `-32001` | The method streams, but was called where a stream cannot be delivered. |
| `-32005` | The server is shutting down. |
| `-32800` | The request was cancelled by the caller. |
