# Getting started

This guide walks through a contract, a server, a client, and the things you will need next: errors, cancellation,
batches and authorization. For the wire-level detail see [Transports](transports.md), [Streaming](streaming.md) and
[OpenRPC](openrpc.md).

## 1. Write a contract

A contract is an interface. It is the single source of truth: the server dispatches to it, the client proxy
implements it, and the OpenRPC document describes it.

```csharp
using JsonCall.Core;

[JsonRpcContract]
public interface IOrders
{
    Task<Order> GetAsync(string id, CancellationToken ct = default);

    Task<Order[]> ListAsync(int limit = 20, string? after = null, CancellationToken ct = default);

    [JsonRpcMethod("orders.cancel")]
    [JsonRpcError(4001, "Order already shipped")]
    Task CancelAsync(string id, CancellationToken ct = default);

    [JsonRpcNotification]
    Task SeenAsync(string id);

    IAsyncEnumerable<OrderEvent> WatchAsync(string id, CancellationToken ct = default);
}
```

| You write | It means |
| --- | --- |
| `GetAsync` | Wire name `get`: camelCase, trailing `Async` removed. `[JsonRpcMethod("...")]` overrides it. |
| `Task<T>`, `ValueTask<T>` | A call with a result. |
| `Task`, `ValueTask`, `void` | A call with no result. The caller still waits for the response. |
| `[JsonRpcNotification]` | A notification: no id is sent and no response comes back. |
| `IAsyncEnumerable<T>` | A stream. See [Streaming](streaming.md). |
| A default value, or a nullable parameter with nothing required after it | An optional param. |
| `CancellationToken` | Never on the wire. On the server it is cancelled when the caller cancels or disconnects. |
| `[JsonRpcContract(ParamStyle = JsonRpcParamStyle.ByName)]` | The proxy sends params as an object. Servers accept both styles regardless. |
| `[JsonRpcError(code, message)]`, `[JsonRpcTag("...")]`, `[Obsolete]` | Documentation that flows into the OpenRPC document. |

Method names starting with `rpc.` are reserved by the JSON-RPC specification and are rejected at registration.

Values are serialized with System.Text.Json using web defaults (camelCase properties) and string enums. Pass your
own `JsonSerializerOptions`, including a source-generated `JsonSerializerContext`, through
`JsonRpcServerOptions.SerializerOptions` on the server and `JsonRpcPeerOptions.SerializerOptions` on the client.

## 2. Serve it

### With ASP.NET Core

```csharp
using JsonCall.AspNetCore;

builder.Services.AddJsonRpc(o => o.AddContract<IOrders, OrderService>());

app.MapJsonRpc("/rpc");
```

`AddContract<TContract, TImplementation>()` registers the implementation as a scoped service unless you already
registered it. Each call gets its own scope, so constructor injection works as it does in a controller. The app fails
at start-up, not at the first call, if two contracts declare the same method name or a contract has no implementation.

`MapJsonRpc` returns an `IEndpointConventionBuilder`, so `.RequireAuthorization()`, `.RequireCors()` and
`.RequireRateLimiting()` apply to the whole endpoint. One route serves three things:

| Request | Behaviour |
| --- | --- |
| `POST` | A single call or a batch. A batch of only notifications returns `204`. |
| `POST` with `Accept: text/event-stream` | A streaming method answers with Server-Sent Events. |
| WebSocket upgrade | A long-lived connection: concurrent calls, streams and server-to-client notifications. |

Restrict an endpoint with `MapJsonRpc("/rpc", o => o.Transports = JsonRpcTransports.Http)`, and add one-off methods
to it with `o.Method("ping", () => "pong")`.

### Without ASP.NET Core

`JsonRpcServer` is transport-neutral. Give it a transport and it serves it:

```csharp
using JsonCall.Server;
using JsonCall.Transport;

var server = new JsonRpcServer(new JsonRpcServerOptions()
    .AddContract<IOrders>(new OrderService())
    .AddMethod("ping", () => "pong"));

// A tool launched by its client, speaking on stdin and stdout (as a language server does):
await using var peer = server.Attach(StdioMessageTransport.FromConsole());
await peer.Completion;

// Or a daemon accepting connections:
using var listener = new TcpMessageListener(IPAddress.Loopback, 4000);
while (true)
    server.Attach(await listener.AcceptAsync());
```

Pass an `IServiceScopeFactory` as the second constructor argument to resolve implementations from a container, one
scope per call. To push notifications to every connection, see
[Broadcasting](transports.md#broadcasting-to-every-connection).

### What a method can ask for

Besides its wire params, a method (or an `AddMethod` delegate) may declare a `CancellationToken` and a
`JsonRpcCallContext`. The context carries the request, the call's services and an `Items` bag; under ASP.NET Core,
`context.Items[JsonRpcHttp.HttpContextKey]` is the `HttpContext`.

## 3. Call it

```csharp
using JsonCall.Client;
using JsonCall.Transport;

await using var client = new JsonRpcClient(
    await WebSocketMessageTransport.ConnectAsync(new Uri("wss://example.com/rpc")));

var orders = client.CreateProxy<IOrders>();
var order = await orders.GetAsync("o-1");
```

Any number of calls can be in flight on one client; responses are matched by id in whatever order they arrive.

Without a contract, call by name:

```csharp
var order = await client.InvokeAsync<Order>("get", JsonRpcParams.ByPosition("o-1"));
await client.NotifyAsync("seen", JsonRpcParams.ByName(new Dictionary<string, object?> { ["id"] = "o-1" }));
```

For a server you do not own, generate the client from its OpenRPC document instead: see [OpenRPC](openrpc.md).

### Notifications from the server

On a stateful transport (everything except HTTP) the server can notify the client, and can call methods on it:

```csharp
client.OnNotification<OrderChanged>("orderChanged", p => Console.WriteLine(p?.Status));
```

The handler receives the whole `params` value, so the type must match its shape: an object type for by-name params
(`{"id": "o-1", "status": "shipped"}`), an array type for by-position params. The untyped overload hands you the
`JsonElement`. A generated client gives you typed `On<Name>` handlers that accept either shape: see
[OpenRPC](openrpc.md#notifications-pushed-by-the-server).

To expose methods to the server, set `JsonRpcPeerOptions.Handler` to a `JsonRpcServer`: a client is a peer, and a
peer can serve as well as call.

## 4. Errors

Throw `JsonRpcApplicationException` to return an application error. The code must be outside the range the
specification reserves (-32768 to -32000).

```csharp
throw new JsonRpcApplicationException(4001, "Order already shipped", new { shippedAt });
```

The caller catches `JsonRpcException`:

```csharp
try
{
    await orders.CancelAsync("o-1");
}
catch (JsonRpcException ex) when (ex.Code == 4001)
{
    var data = (JsonElement?)ex.ErrorData;
}
```

| Situation | What the caller sees |
| --- | --- |
| The method throws `JsonRpcException` | That code, message and data. |
| The method throws anything else | `-32603 Internal error`. The exception is not leaked; `JsonRpcServerOptions.OnUnhandledException` receives it. |
| Unknown method | `-32601`. |
| Params missing, unknown, of the wrong type, or too many | `-32602`, with a message naming the param. |
| The call is not authorized | `-32002` (not authenticated) or `-32003` (forbidden). |
| A streaming method is called where it cannot stream (a batch, or HTTP without `Accept: text/event-stream`) | `-32001`. |
| The call was cancelled | `-32800` on the wire; the caller's own token throws `OperationCanceledException` first. |
| No response within the timeout | `TimeoutException`. |
| The connection drops with calls in flight | `ConnectionLostException` (an `IOException`) on every one. Nothing hangs. |

## 5. Timeouts and cancellation

Calls time out after 30 seconds by default. Change `JsonRpcPeerOptions.DefaultTimeout` (or `client.DefaultTimeout`),
pass a timeout to `InvokeAsync`, or use `Timeout.InfiniteTimeSpan` to disable it.

Cancelling the token you passed, or timing out, sends a `$/cancel` notification on stateful transports and aborts the
request on HTTP. The server method's `CancellationToken` is then cancelled.

## 6. Batches

```csharp
var results = await client.BatchAsync(new[]
{
    new BatchItem("get", JsonRpcParams.ByPosition("o-1")),
    new BatchItem("get", JsonRpcParams.ByPosition("o-2")),
    new BatchItem("seen", JsonRpcParams.ByPosition("o-1"), isNotification: true),
});

var first = results[0].GetResult<Order>();     // throws JsonRpcException if that entry failed
if (!results[1].Succeeded) { /* results[1].Error */ }
```

Results come back in request order, one per non-notification entry. The server runs the entries of a batch
concurrently. Streaming methods cannot be called in a batch.

## 7. Authorization

Under ASP.NET Core, `[Authorize]` and `[AllowAnonymous]` work on a contract interface, a contract method, the
implementation class or an implementation method. They are evaluated per call, including for each entry of a batch
and each call on a WebSocket, using the `HttpContext` of the request or of the WebSocket upgrade.

```csharp
[JsonRpcContract]
[Authorize]
public interface IOrders
{
    [AllowAnonymous]
    Task<string> VersionAsync();

    [Authorize(Roles = "admin")]
    Task CancelAsync(string id, CancellationToken ct = default);
}
```

For anything else that should run before every call, implement `IJsonRpcCallFilter` and add it to
`JsonRpcServerOptions.Filters`. Returning an error rejects the call.
