@phase5
Feature: Typed client generation from OpenRPC documents
  As an integrator
  I want a C# client generated from an OpenRPC document at build time
  So that I get strongly typed calls to services I did not write

  Background:
    Given a project referencing the JsonCall client generator
    And "api.openrpc.json" is included as an AdditionalFile

  @FR-GEN-01
  Scenario: Generates DTOs, contract interface and client
    Given the document defines the methods "add", "getPerson" and the schema "Person"
    When the project is built
    Then a record "Person" is generated
    And an interface "IApiClient" with AddAsync and GetPersonAsync is generated
    And a class "ApiClient" implementing it over JsonRpcClient is generated
    And the project compiles without warnings

  @FR-GEN-01
  Scenario: Generated namespace and names are configurable
    Given the AdditionalFile metadata sets Namespace "Acme.Api" and ClientName "AcmeClient"
    When the project is built
    Then the types are generated in "Acme.Api" with the client named "AcmeClient"

  @FR-GEN-01
  Scenario: Method names are converted to idiomatic C#
    Given the document method name "wallet.get_balance"
    Then the generated method is "GetBalanceAsync" in a "Wallet" group or with the original name preserved in an attribute
    And the wire method name is still "wallet.get_balance"

  @FR-GEN-02
  Scenario Outline: JSON Schema Draft 07 constructs map to C#
    Given a schema using <construct>
    When the client is generated
    Then the C# type is <csharp>

    Examples:
      | construct                                   | csharp                       |
      | type string                                 | string                       |
      | type integer                                | int or long per format       |
      | type number                                 | double or decimal per setting|
      | type boolean                                | bool                         |
      | type array of string                        | IReadOnlyList<string>        |
      | type object with properties                 | a record with those members  |
      | additionalProperties of integer             | IReadOnlyDictionary<string,int> |
      | enum of strings                             | an enum with JSON names      |
      | type ["string","null"]                      | string?                      |
      | format date-time                            | DateTimeOffset               |
      | format uuid                                 | Guid                         |

  @FR-GEN-02
  Scenario: required controls nullability
    Given an object schema with required ["id"] and optional property "nickname"
    Then "Id" is non-nullable and "Nickname" is nullable

  @FR-GEN-02
  Scenario: $ref to components is resolved
    Given a method result of {"$ref":"#/components/schemas/Person"}
    Then the generated return type is Person

  @FR-GEN-02
  Scenario: Cyclic $ref is supported
    Given schema "Node" with property "children" as an array of $ref Node
    When the client is generated
    Then it compiles and round-trips a three-level tree

  @FR-GEN-02
  Scenario: allOf composition
    Given schema "Employee" is allOf [$ref Person, {properties: {salary}}]
    Then Employee exposes the members of Person and "Salary"

  @FR-GEN-02
  Scenario: oneOf with a discriminator
    Given a oneOf schema whose variants share a "kind" const property
    Then an abstract base type with one subtype per variant is generated
    And deserialization selects the subtype by "kind"

  @FR-GEN-02
  Scenario: oneOf without a discriminator falls back to a documented wrapper
    Given a oneOf of string and integer
    Then a union wrapper exposing the matched variant is generated
    And a diagnostic of severity Info explains the fallback

  @FR-GEN-02
  Scenario: Unsupported keyword produces a diagnostic, not a crash
    Given a schema using "patternProperties"
    When the project is built
    Then a warning with the JSON path is reported
    And the member falls back to JsonElement

  @FR-GEN-02
  Scenario: Boolean schemas
    Given a param schema of true
    Then the generated parameter type is JsonElement

  @FR-GEN-03
  Scenario: Optional parameters become optional arguments
    Given the document param "greeting" is not required
    Then the generated method has "string? greeting = null"
    And a call omitting it sends params without "greeting"

  @FR-GEN-03
  Scenario Outline: Parameter structure follows paramStructure
    Given the method declares paramStructure "<structure>"
    When the generated client calls it
    Then the wire params are <wire>

    Examples:
      | structure   | wire        |
      | by-name     | an object   |
      | by-position | an array    |
      | either      | per client default |

  @FR-GEN-03
  Scenario: Notifications return Task without awaiting a response
    Given a method with no result
    Then the generated method sends a notification and returns a Task

  @FR-GEN-03
  Scenario: Documented errors become typed exceptions
    Given the method documents an error with code 404
    When the server returns code 404
    Then the generated client throws a typed exception exposing the error data

  @FR-GEN-03
  Scenario: Generated client is transport and serializer agnostic
    When the generated client is constructed over a websocket JsonRpcClient and then a tcp JsonRpcClient
    Then the same calls succeed over both

  @FR-GEN-03
  Scenario: Streaming methods become IAsyncEnumerable
    Given a method with "x-jsoncall-stream": true and result schema Tick
    Then the generated method returns IAsyncEnumerable<Tick> and accepts a CancellationToken

  @FR-GEN-03
  Scenario: Deprecated methods are marked Obsolete
    Given a method with "deprecated": true
    Then the generated method has [Obsolete]

  @FR-GEN-03
  Scenario: XML documentation is emitted from descriptions
    Given method and param summaries in the document
    Then the generated members carry matching XML doc comments

  @FR-GEN-03
  Scenario: AOT-friendly output
    When the project is built
    Then a JsonSerializerContext covering all generated DTOs is generated
    And the client uses it by default

  @FR-GEN-04
  Scenario: Output is deterministic
    When the generator runs twice on the same document
    Then the outputs are byte-identical

  @FR-GEN-04
  Scenario: Invalid document reports a precise diagnostic
    Given the document has a method with a duplicate param name
    When the project is built
    Then error "JSONCALL0xx" is reported at the JSON path "methods[2].params[1].name"
    And the file and line of the AdditionalFile are included

  @FR-GEN-04
  Scenario: Unsupported OpenRPC version
    Given the document declares "openrpc": "9.0.0"
    Then a warning states the supported versions and generation continues best-effort

  @FR-GEN-04
  Scenario: Incremental generation only reruns on change
    Given the project has been built once
    When an unrelated source file is edited
    Then the generator does not execute again

  @FR-GEN-04
  Scenario: Command line tool produces the same output as the source generator
    When "jsoncall generate api.openrpc.json --out ./gen" is run
    Then the files equal the source generator output

  @FR-GEN-04
  Scenario: Remote documents can be fetched by the CLI
    When "jsoncall generate --url https://example.test/rpc --discover" is run
    Then the CLI calls "rpc.discover", saves the document and generates the client

  @FR-GEN-05
  Scenario: Contract to OpenRPC to client round trip
    Given the reference server contract IMath is hosted
    When its OpenRPC document is generated and a client is generated from it
    Then every generated client method calls the live server successfully
    And generated DTOs serialize to the same JSON as the server's DTOs

  @FR-GEN-05
  Scenario Outline: Third-party documents generate compiling clients
    Given the published OpenRPC document "<document>"
    When the client is generated
    Then the generated code compiles with no errors

    Examples:
      | document                          |
      | petstore-openrpc.json             |
      | ethereum-execution-apis.json      |
      | link-example-openrpc.json         |

  # Schemas split across files: {"$ref": "event.schema.json"}. A source generator only sees AdditionalFiles, so the
  # sibling schemas must be added as AdditionalFiles too; the command line tool reads them from the document's directory.
  @FR-GEN-06 @phase6
  Scenario: Sibling schema files resolve through AdditionalFiles
    Given a method "probe" whose result schema is {"$ref":"event.schema.json"}
    And the sibling schema "event.schema.json" is included as an AdditionalFile with title "Daemon event" and properties "type" and "at"
    When the client is generated
    Then a record "DaemonEvent" with the properties "Type" and "At" is generated
    And the probe result type is "DaemonEvent"
    And no JSONCALL006 diagnostic is reported
    And the generated code compiles with no errors

  @FR-GEN-06 @phase6
  Scenario: A sibling schema is found by its path relative to the document
    Given a method "probe" whose result schema is {"$ref":"schemas/hit.schema.json"}
    And the sibling schema "schemas/hit.schema.json" is included as an AdditionalFile with title "hit" and properties "id" and "rank"
    When the client is generated
    Then a record "Hit" with the properties "Id" and "Rank" is generated
    And no JSONCALL006 diagnostic is reported

  @FR-GEN-06 @phase6
  Scenario: Nested pointers into a sibling schema are followed
    Given a method "probe" whose result schema is {"$ref":"shapes.schema.json#/definitions/point"}
    And the sibling schema "shapes.schema.json" is included as an AdditionalFile with a definition "point" with properties "x" and "y"
    When the client is generated
    Then a record "Point" with the properties "X" and "Y" is generated
    And no JSONCALL006 diagnostic is reported

  @FR-GEN-06 @phase6
  Scenario: Sibling schemas that refer to each other, and to themselves, resolve without looping
    Given a method "probe" whose result schema is {"$ref":"a.schema.json"}
    And the sibling schemas "a.schema.json" and "b.schema.json" refer to each other
    When the client is generated
    Then the generated code compiles with no errors
    And the records "A" and "B" are generated
    And no JSONCALL006 diagnostic is reported

  @FR-GEN-06 @phase6
  Scenario: A sibling schema that is not an AdditionalFile keeps the JsonElement fallback
    Given a method "probe" whose result schema is {"$ref":"missing.schema.json"}
    When the client is generated
    Then a JSONCALL006 warning is reported at the JSON path "methods[0].result.schema.$ref"
    And the probe result type is "JsonElement"

  @FR-GEN-06 @phase6
  Scenario: A pointer that leads nowhere is reported with its JSON path
    Given a method "probe" whose result schema is {"$ref":"shapes.schema.json#/definitions/nope"}
    And the sibling schema "shapes.schema.json" is included as an AdditionalFile with a definition "point" with properties "x" and "y"
    When the client is generated
    Then a JSONCALL006 warning is reported at the JSON path "methods[0].result.schema.$ref"

  @FR-GEN-06 @phase6
  Scenario: Editing a sibling schema regenerates the client
    Given a method "probe" whose result schema is {"$ref":"event.schema.json"}
    And the sibling schema "event.schema.json" is included as an AdditionalFile with title "Daemon event" and properties "type" and "at"
    And the project has been built once with the sibling schema
    When the sibling schema gains the property "extra"
    Then the generator executes again and the record has the property "Extra"

  @FR-GEN-06 @phase6
  Scenario: The command line tool resolves sibling schemas from the document's directory
    Given a directory holding "api.openrpc.json" with a method "probe" whose result is {"$ref":"event.schema.json"}
    And the directory also holds "event.schema.json" with title "Daemon event" and properties "type" and "at"
    When "jsoncall generate api.openrpc.json --out ./gen" is run in that directory
    Then the generated client has a record "DaemonEvent"
    And the tool output reports that "event.schema.json" was loaded
    And the tool output has no JSONCALL006 warning

  # Real-world contracts: subsets of two standard protocols written as OpenRPC, with their types in sibling schema
  # files the way the protocols publish them (Support/Data/lsp and Support/Data/acp). Golden files lock the output.
  @FR-GEN-06 @phase6 @lsp
  Scenario: The Language Server Protocol contract generates fully typed from its sibling schemas
    Given the contract "lsp/lsp.openrpc.json" with its sibling schemas "lsp/basic.schema.json", "lsp/diagnostic.schema.json" and "lsp/window.schema.json"
    And the client is named "LanguageServerClient" in the namespace "Lsp"
    When the client is generated
    Then the records "Diagnostic" and "Range" are generated
    And the records "Hover" and "WorkDoneProgressBegin" are generated
    And no JSONCALL006 diagnostic is reported
    And the generated code compiles with no errors
    And the generated client matches the checked-in golden "lsp/LanguageServerClient.g.cs.txt" byte for byte

  @FR-GEN-06 @phase6 @acp
  Scenario: The Agent Client Protocol contract generates fully typed from its $defs schema
    Given the contract "acp/acp.openrpc.json" with its sibling schemas "acp/schema.json"
    And the client is named "AgentClient" in the namespace "Acp"
    When the client is generated
    Then the records "SessionUpdate" and "ContentBlock" are generated
    And the records "InitializeResponse" and "McpServer" are generated
    And no JSONCALL006 diagnostic is reported
    And the generated code compiles with no errors
    And the generated client matches the checked-in golden "acp/AgentClient.g.cs.txt" byte for byte
