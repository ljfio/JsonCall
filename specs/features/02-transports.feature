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

  @FR-TRANS-02 @FR-TRANS-03
  Scenario Outline: TCP framing handles a message split across reads
    Given a client connected over tcp using <framing> framing
    And the network delivers each message one byte at a time
    When the client calls "echo" with ["split"]
    Then the result is "split"

    Examples:
      | framing        |
      | content-length |
      | newline        |

  @FR-TRANS-02 @FR-TRANS-03
  Scenario Outline: TCP framing handles several messages in one read
    Given a client connected over tcp using <framing> framing
    When the server writes 3 framed responses in a single socket write
    Then the client receives 3 distinct messages in order

    Examples:
      | framing        |
      | content-length |
      | newline        |

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

  @FR-TRANS-02 @FR-TRANS-03
  Scenario Outline: Large messages cross newline framing boundaries intact
    Given a client connected over <transport> using newline framing
    When the client calls "echo" with a 20 MB string
    Then the result equals the sent string

    Examples:
      | transport |
      | tcp       |
      | stdio     |

  @FR-TRANS-03 @FR-TRANS-04
  Scenario Outline: Same call over a stream transport with either framing
    Given a client connected over <transport> using <framing> framing
    When the client calls "echo" with ["hello"]
    Then the result is "hello"

    Examples:
      | transport | framing        |
      | tcp       | content-length |
      | tcp       | newline        |
      | stdio     | content-length |
      | stdio     | newline        |

    @unix-only
    Examples:
      | transport | framing        |
      | uds       | content-length |
      | uds       | newline        |

  @FR-TRANS-03 @FR-TRANS-04
  Scenario Outline: Calls and notifications interleave on one connection
    Given a client connected over <transport> using <framing> framing
    When the client issues 100 concurrent "echo" calls while the server sends 100 notifications
    Then every call returns its own argument
    And the client receives the 100 notifications in order

    Examples:
      | transport | framing        |
      | tcp       | content-length |
      | tcp       | newline        |
      | stdio     | newline        |

    @unix-only
    Examples:
      | transport | framing        |
      | uds       | content-length |
      | uds       | newline        |

  @FR-TRANS-03
  Scenario Outline: Newline framing accepts CRLF and blank lines
    Given a newline framed peer sending <wire>
    Then the transport receives the messages ["{\"a\":1}","{\"b\":2}"]

    Examples:
      | wire                        |
      | LF line endings             |
      | CRLF line endings           |
      | blank lines between messages |

  @FR-TRANS-03
  Scenario: A Content-Length frame sent to a newline reader is a framing error
    Given a newline framed peer sending a "Content-Length: 2" header followed by "{}"
    Then the connection reports a framing error
    And no further messages are processed

  @FR-TRANS-03
  Scenario: A line over the maximum message size is rejected
    Given a newline framed transport with a maximum message size of 100 bytes
    And a peer sending a line of 101 bytes
    Then the transport reports that the message is too large
    And no further messages are processed

  @FR-TRANS-03
  Scenario: A message exactly at the maximum size is accepted
    Given a newline framed transport with a maximum message size of 100 bytes
    And a peer sending a line of 100 bytes terminated by CRLF
    Then the transport receives a message of 100 bytes

  @FR-TRANS-03
  Scenario: A connection closing in the middle of a line is a lost connection
    Given a newline framed peer sending a partial message and then closing
    Then the transport reports a lost connection

  @FR-TRANS-03
  Scenario: A clean close after the last newline ends the stream
    Given a newline framed peer sending 2 messages and then closing
    Then the transport receives 2 messages and then the end of the stream

  @FR-TRANS-03
  Scenario: A message containing a raw newline cannot be sent with newline framing
    Given a newline framed transport
    When a message containing a raw newline is sent
    Then the send fails with a framing error

  @FR-TRANS-02
  Scenario: Frames with a missing or invalid Content-Length are rejected
    Given a stdio peer sending a frame with "Content-Length: abc"
    Then the connection reports a framing error
    And no further messages are processed

  @FR-TRANS-02 @FR-TRANS-03
  Scenario Outline: Stdio transport leaves standard error untouched
    Given a client connected over stdio using <framing> framing to a child process that logs to standard error
    When the client calls "echo" with ["x"]
    Then the result is "x"
    And the log output is available on the transport's diagnostics stream

    Examples:
      | framing        |
      | content-length |
      | newline        |

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

  @FR-TRANS-04 @unix-only
  Scenario Outline: Transport closed by the peer mid-call over a Unix domain socket
    Given a client connected over uds using <framing> framing
    And a call to "slow" is in flight
    When the peer closes the connection
    Then the in-flight call fails with a ConnectionLostException
    And no call hangs

    Examples:
      | framing        |
      | content-length |
      | newline        |

  @FR-TRANS-04 @unix-only
  Scenario: Connecting to a socket nobody listens on fails with ConnectionLostException
    Given no process is listening on a Unix socket path
    When a client connects to it
    Then the connect fails with a ConnectionLostException

  @FR-TRANS-04 @unix-only
  Scenario: Connecting to a stale socket file fails with ConnectionLostException
    Given a Unix socket file left behind by a listener that did not clean up
    When a client connects to it
    Then the connect fails with a ConnectionLostException

  @FR-TRANS-04
  Scenario: Connecting to a TCP port nobody listens on fails with ConnectionLostException
    Given no process is listening on a TCP port
    When a client connects to it over tcp
    Then the connect fails with a ConnectionLostException

  @FR-TRANS-04 @unix-only
  Scenario: The listener removes its socket file when disposed
    Given a Unix socket listener
    Then the socket file exists
    When the listener is disposed
    Then the socket file no longer exists

  @FR-TRANS-04 @unix-only
  Scenario: A listener replaces a stale socket file but not a live one
    Given a Unix socket file left behind by a listener that did not clean up
    When a new listener is created at the same path
    Then it accepts a client connection
    But a second listener at the same path is refused
