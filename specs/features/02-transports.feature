@phase1
Feature: Interchangeable transports
  As a developer
  I want to swap the transport under a client without changing protocol code
  So that the same calls work over WebSocket, TCP, stdio and HTTP

  Background:
    Given an echo server exposing the method "echo" that returns its first parameter

  @FR-TRANS-01
  Scenario Outline: Same call, any transport
    Given a client connected over <transport>
    When the client calls "echo" with ["hello"]
    Then the result is "hello"

    Examples:
      | transport  |
      | in-memory  |
      | websocket  |
      | tcp        |
      | stdio      |
      | http       |

  @FR-TRANS-01
  Scenario Outline: Same call, any serializer on a message transport
    Given a client connected over <transport> using the <serializer> serializer
    When the client calls "echo" with [{"id":1,"tags":["a","b"]}]
    Then the result equals the sent value

    Examples:
      | transport | serializer  |
      | websocket | json        |
      | websocket | cbor        |
      | websocket | messagepack |
      | tcp       | json        |
      | tcp       | messagepack |

  @FR-TRANS-02
  Scenario: TCP framing handles a message split across reads
    Given a client connected over tcp
    And the network delivers each message one byte at a time
    When the client calls "echo" with ["split"]
    Then the result is "split"

  @FR-TRANS-02
  Scenario: TCP framing handles several messages in one read
    Given a client connected over tcp
    When the server writes 3 framed responses in a single socket write
    Then the client receives 3 distinct messages in order

  @FR-TRANS-02
  Scenario Outline: Large messages cross framing boundaries intact
    Given a client connected over <transport>
    When the client calls "echo" with a 20 MB string
    Then the result equals the sent string

    Examples:
      | transport |
      | websocket |
      | tcp       |
      | stdio     |

  @FR-TRANS-02
  Scenario: Frames with a missing or invalid Content-Length are rejected
    Given a stdio peer sending a frame with "Content-Length: abc"
    Then the connection reports a framing error
    And no further messages are processed

  @FR-TRANS-02
  Scenario: Stdio transport leaves standard error untouched
    Given a client connected over stdio to a child process that logs to standard error
    When the client calls "echo" with ["x"]
    Then the result is "x"
    And the log output is available on the transport's diagnostics stream

  @FR-TRANS-01
  Scenario Outline: Text and binary WebSocket messages follow the serializer
    Given a client connected over websocket using the <serializer> serializer
    Then the transport sends <frame type> frames

    Examples:
      | serializer  | frame type |
      | json        | text       |
      | cbor        | binary     |
      | messagepack | binary     |

  @FR-TRANS-01
  Scenario: A custom transport can be plugged in
    Given a user-defined transport implementing IMessageTransport
    When the client is constructed with it and calls "echo" with ["custom"]
    Then the result is "custom"

  @FR-CLIENT-05
  Scenario Outline: Transport closed by the peer mid-call
    Given a client connected over <transport>
    And a call to "slow" is in flight
    When the peer closes the connection
    Then the in-flight call fails with a ConnectionLostException
    And no call hangs

    Examples:
      | transport |
      | websocket |
      | tcp       |
      | stdio     |
