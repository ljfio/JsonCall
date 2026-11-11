@phase4
Feature: OpenRPC document generation from contracts
  As a service author
  I want an OpenRPC document generated from my C# contract
  So that consumers and tooling can discover and document my API

  Background:
    Given the contract
      """
      /// <summary>Basic maths service.</summary>
      [JsonRpcContract(Title = "Math API", Version = "1.2.0")]
      public interface IMath
      {
          /// <summary>Adds two numbers.</summary>
          /// <param name="a">First addend.</param>
          /// <param name="b">Second addend.</param>
          Task<int> AddAsync(int a, int b);

          Task<string> GreetAsync(string name, string greeting = "Hello");

          [Obsolete("Use AddAsync")] Task<int> PlusAsync(int a, int b);

          [JsonRpcNotification] Task LogAsync(string message);

          Task<Person> GetPersonAsync(int id);
          Task<IReadOnlyList<Person>> ListPeopleAsync(Person? filter = null);
      }
      """
    And the document is generated

  @FR-ORPC-01
  Scenario: Root document is well formed
    Then "openrpc" is "1.4.1" or the library's declared supported version
    And "info.title" is "Math API" and "info.version" is "1.2.0"
    And "methods" contains an entry for each contract method

  @FR-ORPC-01
  Scenario: Documentation comes from XML docs
    Then the method "add" has summary "Adds two numbers."
    And its param "a" has description "First addend."

  @FR-ORPC-01
  Scenario: Method names follow the configured naming policy
    Given the naming policy is camelCase without the "Async" suffix
    Then the methods are named "add", "greet", "plus", "log", "getPerson" and "listPeople"

  @FR-ORPC-01
  Scenario: Method names can be overridden by attribute
    Given a method annotated [JsonRpcMethod("math.add")]
    Then the document method name is "math.add"

  @FR-ORPC-02
  Scenario: Required and optional parameters
    Then "greet" lists "name" as required and "greeting" as not required
    And all required params come before optional params
    And "greeting" has default "Hello" in its schema

  @FR-ORPC-02
  Scenario: Nullable reference parameter is optional
    Then "listPeople" param "filter" is not required

  @FR-ORPC-02
  Scenario Outline: paramStructure follows contract settings
    Given the contract is configured with params <setting>
    Then every method has paramStructure "<structure>"

    Examples:
      | setting     | structure   |
      | by-name     | by-name     |
      | by-position | by-position |
      | either      | either      |

  @FR-ORPC-03
  Scenario: Notification methods have no result
    Then "log" has no "result" member

  @FR-ORPC-04
  Scenario: Primitive schemas
    Then the schema of "add" param "a" is {"type":"integer","format":"int32"}
    And the schema of "greet" result is {"type":"string"}

  @FR-ORPC-04
  Scenario: Shared types are hoisted into components
    Then "components.schemas.Person" exists
    And "getPerson" result schema is {"$ref":"#/components/schemas/Person"}
    And "listPeople" result schema is an array of {"$ref":"#/components/schemas/Person"}

  @FR-ORPC-04
  Scenario: Recursive types are supported
    Given a type Node with a property Children of type List<Node>
    Then the Node schema references itself with $ref and generation terminates

  @FR-ORPC-04
  Scenario: Schemas use JSON Schema Draft 07
    Then no schema uses a keyword that is not valid in Draft 07
    And the document declares "$schema" for Draft 07 where schemas are embedded

  @FR-ORPC-04
  Scenario Outline: Common .NET types map to schemas
    Given a parameter of type <type>
    Then its schema is <schema>

    Examples:
      | type            | schema                                         |
      | Guid            | {"type":"string","format":"uuid"}              |
      | DateTimeOffset  | {"type":"string","format":"date-time"}         |
      | TimeSpan        | {"type":"string"}                              |
      | enum Color      | {"type":"string","enum":["Red","Green"]}       |
      | int?            | {"type":["integer","null"]}                    |
      | byte[]          | {"type":"string","contentEncoding":"base64"}   |
      | Dictionary<string,int> | {"type":"object","additionalProperties":{"type":"integer"}} |

  @FR-ORPC-04
  Scenario: Serializer options affect the schema
    Given the JSON naming policy is camelCase
    Then property names in schemas are camelCase

  @FR-ORPC-05
  Scenario: Obsolete methods are deprecated
    Then "plus" has "deprecated": true
    And its description includes the obsolete message "Use AddAsync"

  @FR-ORPC-05
  Scenario: Declared errors are documented
    Given "getPerson" is annotated [JsonRpcError(404, "Person not found")]
    Then "getPerson" has an error with code 404 and message "Person not found"

  @FR-ORPC-05
  Scenario: Error codes must be unique per method and outside the reserved range
    Given "getPerson" declares errors with codes 404 and 404, or an error with code -32010
    When the document is generated
    Then generation fails with a diagnostic naming the method and code

  @FR-ORPC-01
  Scenario: Tags group methods
    Given the contract is annotated [JsonRpcTag("math")]
    Then every method in it has the tag "math"

  @FR-ORPC-01
  Scenario: Servers section reflects the hosted endpoints
    Given the app maps app.MapJsonRpc("/rpc") and is configured with its public base URL
    Then "servers" contains an entry for that URL

  @FR-ORPC-06
  Scenario: Document is served on rpc.discover
    Given an ASP.NET Core host with the OpenRPC module enabled
    When a client calls "rpc.discover"
    Then the result is the generated document
    And repeated calls return an identical cached document

  @FR-ORPC-06
  Scenario Outline: rpc.discover works on every transport
    Given the client is connected over <transport>
    When it calls "rpc.discover"
    Then it receives the document

    Examples:
      | transport |
      | http      |
      | websocket |
      | tcp       |

  @FR-ORPC-06
  Scenario: Generated document validates against the official meta-schema
    When the document is validated against the OpenRPC 1.4 meta-schema
    Then there are no validation errors

  @FR-ORPC-06
  Scenario: Golden file stability
    When the document for the reference contract is generated
    Then it matches the checked-in golden "math.openrpc.json" byte for byte

  @FR-ORPC-06
  Scenario: rpc.discover can be disabled or protected
    Given discovery is configured to require authorization
    When an anonymous client calls "rpc.discover"
    Then it receives an authorization error

  @FR-ORPC-07
  Scenario: Streaming methods are marked
    Given a contract method returning IAsyncEnumerable<Tick>
    Then the method has "x-jsoncall-stream": true
    And its result schema is the Tick schema
