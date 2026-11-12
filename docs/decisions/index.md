# Index

Directory: `decisions/`

## Concepts

| Title | Type | Description |
|-------|------|-------------|
| [0000: Project knowledge is an OKF bundle; changes are GitHub issues](0000-knowledge-bundle.md) | Decision | docs/ is an OKF bundle; changes are tracked as GitHub issues. |
| [0001: Own wire layer rather than building on StreamJsonRpc](0001-own-wire-layer.md) | Decision | Implement JSON-RPC ourselves instead of wrapping StreamJsonRpc. |
| [0002: Streaming wire convention](0002-stream-wire-convention.md) | Decision | $/stream.* notifications, SSE over HTTP, x-jsoncall-stream. |
| [0003: Own JSON Schema to C# mapper in the generator](0003-schema-to-csharp-mapper.md) | Decision | The generator maps JSON Schema to C# with its own mapper. |
| [0004: Own Draft 07 schema generator for OpenRPC output](0004-draft07-schema-generator.md) | Decision | Draft 07 schemas from our own mapper, not JsonSchemaExporter. |
| [0005: Mapping oneOf and anyOf to C#](0005-union-mapping.md) | Decision | oneOf/anyOf: polymorphic records, collapse, or a union wrapper. |
| [0006: Reqnroll for executable specifications](0006-reqnroll.md) | Decision | Gherkin feature files run through Reqnroll on xUnit. |
| [0007: One transport package, and wire formats as JSON transcoders](0007-package-layout.md) | Decision | One transport package; wire formats transcode to canonical JSON. |
| [0008: Generated documents declare OpenRPC 1.3.2](0008-openrpc-version.md) | Decision | Documents declare OpenRPC 1.3.2; the generator reads 1.0 to 1.4. |
| [0009: The name JsonCall](0009-name.md) | Decision | JsonCall, and the names derived from it. |

