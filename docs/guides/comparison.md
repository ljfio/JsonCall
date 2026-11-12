---
type: Guide
title: How JsonCall compares
description: "JsonCall against StreamJsonRpc and others, and when to choose each."
tags: [guide, positioning]
---

# How JsonCall compares

An honest positioning against the .NET JSON-RPC libraries people already use, written in November 2026. Other
libraries move; check their current documentation before relying on a row here.

## In one paragraph

**StreamJsonRpc** is the reference JSON-RPC library for .NET and the right default for two processes talking over a
pipe or socket, especially inside the Visual Studio ecosystem. JsonCall is for the cases it does not cover: a
JSON-RPC API served from **ASP.NET Core** over HTTP as well as sockets, an API that must be **described** (OpenRPC),
and a third-party API you want to **consume from its description**. If none of those three applies to you,
StreamJsonRpc is the safer choice today: it is mature, widely deployed and multi-targeted, and JsonCall is a preview.

## Feature comparison

| | JsonCall | StreamJsonRpc | Tochka.JsonRpc | EdjCase.JsonRpc |
| --- | --- | --- | --- | --- |
| Maintainer | Individual | Microsoft | Tochka | Individual |
| Maturity | Preview | Mature, very widely used | Mature | Mature |
| Target frameworks | .NET 10 only | .NET Standard 2.0 and current .NET | Current .NET | Current .NET |
| Client | Yes | Yes | Yes (HTTP) | Yes (HTTP) |
| Server | Yes | Yes | Yes (ASP.NET Core) | Yes (ASP.NET Core) |
| HTTP POST server | Yes | No | Yes | Yes |
| WebSocket | Yes | Yes | No | No |
| stdio, pipes, TCP, Unix sockets | Yes | Yes | No | No |
| Batches | Yes | No | Yes | Yes |
| Calls in both directions on one connection | Yes | Yes | No | No |
| `IAsyncEnumerable<T>` streaming | Yes (own convention) | Yes (own convention) | No | No |
| `IProgress<T>`, `Stream` and pipe arguments, marshaled objects | No | Yes | No | No |
| Wire formats | JSON, CBOR, MessagePack | JSON, MessagePack | JSON | JSON |
| ASP.NET Core DI and `[Authorize]` per method | Yes | No | Yes | Yes |
| OpenRPC document generation | Yes | No | Yes | No |
| Typed client generated from OpenRPC | Yes | No | No | No |
| Logging, tracing, metrics | No | Yes (`TraceSource`, activity tracing) | Via ASP.NET Core | Via ASP.NET Core |
| Trimming and Native AOT | Generated clients only | Supported with source-generated proxies | No | No |

Also in this space, but aimed at one protocol rather than at JSON-RPC in general: the **Model Context Protocol C#
SDK**, **OmniSharp's** language server libraries, and **Nethereum** for Ethereum. If you are implementing exactly one
of those protocols, use its SDK.

## Where JsonCall is ahead

- **One contract, every transport.** The same interface is served over HTTP, SSE, WebSocket, TCP, Unix sockets and
  stdio, and called over any of them, with no code change.
- **OpenRPC both ways.** No other .NET library both emits OpenRPC from code and generates typed C# from it. The
  generator handles documents written by other people: `oneOf` unions, cycles, sibling schema files.
- **A real ASP.NET Core endpoint.** Routing, DI scopes per call, `[Authorize]` per method, CORS and rate limiting
  through the normal endpoint conventions.
- **Complete JSON-RPC 2.0**, including batches, which StreamJsonRpc does not implement.
- **Three wire formats** behind one seam.
- **Executable specifications.** Every requirement is a Gherkin scenario that runs in CI.

## Where JsonCall is behind

These are the gaps a team evaluating the package will find. Each is on the [roadmap](../product/roadmap.md).

- **Track record.** No releases, no production users, one maintainer.
- **Target frameworks.** .NET 10 only. Top-tier libraries support the current LTS releases, and often
  .NET Standard 2.0.
- **Observability.** No `ILogger`, `ActivitySource` or `Meter`. You cannot see a call in a trace or a log without
  writing a filter.
- **Trimming and Native AOT.** The typed proxy and the server dispatcher use reflection. Only clients generated
  from OpenRPC are AOT-safe.
- **Performance.** Unmeasured. Messages are copied several times between transport, serializer and protocol, and
  the server invokes methods through reflection.
- **Interoperability of streaming.** The stream convention is JsonCall's own, as StreamJsonRpc's is its own. A
  JsonCall stream can only be consumed by a JsonCall client or by one written to [the convention](streaming.md).
- **API stability.** The public surface has not been reviewed or frozen, and 233 public members have no XML
  documentation.
- **Richer marshaling.** No `IProgress<T>`, no `Stream` arguments, no object references across the wire.

## Choosing

| If you need | Use |
| --- | --- |
| Two .NET processes, or .NET and VS Code or Visual Studio, over a pipe or socket | StreamJsonRpc |
| .NET Framework or .NET Standard 2.0 | StreamJsonRpc |
| `IProgress<T>` or stream arguments | StreamJsonRpc |
| A production HTTP JSON-RPC API today, on a mature library | Tochka.JsonRpc or EdjCase.JsonRpc |
| An MCP server or client | The Model Context Protocol C# SDK |
| One contract over HTTP *and* sockets | JsonCall |
| A typed C# client for a service that publishes OpenRPC | JsonCall |
| A service that publishes OpenRPC and streams | JsonCall |
| Batches over a socket | JsonCall |
