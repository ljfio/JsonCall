@phase1
Feature: Pluggable serialization
  As a developer
  I want to choose the wire format independently of the transport
  So that I can trade readability for size and speed

  @FR-SER-01
  Scenario Outline: Request round trip
    Given the <serializer> serializer
    And a request with method "sum", id "42" and params <params>
    When the request is serialized and deserialized
    Then the deserialized request equals the original

    Examples:
      | serializer  | params                         |
      | json        | [1,2,3]                        |
      | json        | {"a":1,"b":"x"}                |
      | cbor        | [1,2,3]                        |
      | cbor        | {"a":1,"b":"x"}                |
      | messagepack | [1,2,3]                        |
      | messagepack | {"a":1,"b":"x"}                |

  @FR-SER-01
  Scenario Outline: Response round trip with a typed result
    Given the <serializer> serializer
    And a success response with id 1 and result {"name":"Ada","age":36}
    When the response is serialized and deserialized as a typed Person
    Then the Person has name "Ada" and age 36

    Examples:
      | serializer  |
      | json        |
      | cbor        |
      | messagepack |

  @FR-SER-01
  Scenario Outline: Error response round trip
    Given the <serializer> serializer
    And an error response with id 1, code 1001, message "Quota exceeded" and data {"limit":5}
    When the response is serialized and deserialized
    Then the error code, message and data are preserved

    Examples:
      | serializer  |
      | json        |
      | cbor        |
      | messagepack |

  @FR-SER-02
  Scenario Outline: Params structure is preserved
    Given the <serializer> serializer
    When a request with by-<structure> params is round-tripped
    Then the params are still by-<structure>
    And values keep their order or keys

    Examples:
      | serializer  | structure |
      | json        | position  |
      | json        | name      |
      | cbor        | position  |
      | cbor        | name      |
      | messagepack | position  |
      | messagepack | name      |

  @FR-SER-02
  Scenario: Params can be built without an existing collection
    When params are created from a single value, a params array and a dictionary
    Then each produces the expected by-position or by-name structure

  @FR-SER-02
  Scenario: Absent params stay absent
    Given a request without params
    When it is serialized with each serializer
    Then no "params" member is written

  @FR-SER-03
  Scenario: Custom System.Text.Json options are honoured
    Given serializer options with camelCase naming and string enums
    When a request with an enum parameter is serialized
    Then the JSON uses camelCase member names and the enum as a string

  @FR-SER-03 @NFR-02
  Scenario: Source-generated serializer context works without reflection
    Given a JsonSerializerContext covering the contract's types
    When a call is made with trimming and reflection-based serialization disabled
    Then the call succeeds

  @FR-SER-04
  Scenario Outline: Malformed input yields a parse error and no exception escapes
    Given the <serializer> serializer
    When the bytes <bytes> are deserialized as a message
    Then a parse error with code -32700 is produced
    And no unhandled exception is thrown

    Examples:
      | serializer  | bytes              |
      | json        | truncated JSON     |
      | json        | invalid UTF-8      |
      | cbor        | truncated CBOR     |
      | messagepack | truncated payload  |

  @FR-SER-04
  Scenario: Oversized message is rejected before it is fully buffered
    Given a maximum message size of 1 MB
    When a 5 MB message is received
    Then the connection reports a message-too-large error
    And memory use stays bounded

  @FR-SER-01
  Scenario: Same logical message is equivalent across serializers
    Given the request "sum" with params [1,2,3]
    When it is encoded with json, cbor and messagepack and decoded again
    Then all three decoded requests are equal
