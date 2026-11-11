# Jason

A simple modular JSON-RPC library for .NET

## Modules

| Serializer      | Package |
| --------------- | ------- |
| System JSON     |         |
| Newtonsoft JSON |         |
| Cbor            |         |
| Messagepack     |         |


| Clients      | Package |
| ------------ | ------- |
| Websocket    |         |
| Command Line |         |
## Specifications

Acceptance criteria live in [`specs/features`](specs/features) as Gherkin and run via Reqnroll + xUnit in `test/JsonCall.Specs`.
`dotnet test` reports specs without step definitions as skipped; that count is the remaining backlog.
Step definitions go in `test/JsonCall.Specs/Steps`.
