@phase1
Feature: JSON-RPC 2.0 protocol conformance
  As a service author or integrator
  I want the library to follow the JSON-RPC 2.0 specification exactly
  So that it interoperates with any other conforming implementation

  Background:
    Given a server exposing the methods "subtract", "sum", "get_data", "update" and "notify_hello"

  @FR-PROTO-01 @FR-PROTO-04
  Scenario: Call with positional parameters
    When the client sends '{"jsonrpc":"2.0","method":"subtract","params":[42,23],"id":1}'
    Then the server responds with '{"jsonrpc":"2.0","result":19,"id":1}'

  @FR-PROTO-04
  Scenario: Call with named parameters
    When the client sends '{"jsonrpc":"2.0","method":"subtract","params":{"subtrahend":23,"minuend":42},"id":3}'
    Then the server responds with '{"jsonrpc":"2.0","result":19,"id":3}'

  @FR-PROTO-04
  Scenario: Call with parameters omitted
    When the client sends '{"jsonrpc":"2.0","method":"get_data","id":9}'
    Then the server responds with a result for id 9

  @FR-PROTO-02
  Scenario Outline: Id of any permitted type is echoed verbatim
    When the client sends '{"jsonrpc":"2.0","method":"sum","params":[1,2],"id":<id>}'
    Then the response id is exactly <id>

    Examples:
      | id       |
      | 7        |
      | "abc-1"  |
      | null     |

  @FR-PROTO-03
  Scenario: Notification produces no response
    When the client sends '{"jsonrpc":"2.0","method":"update","params":[1,2,3]}'
    Then the server sends no response
    And the "update" handler is invoked once with [1,2,3]

  @FR-PROTO-03
  Scenario: Notification to an unknown method produces no error response
    When the client sends '{"jsonrpc":"2.0","method":"foobar"}'
    Then the server sends no response

  @FR-PROTO-06
  Scenario: Method not found
    When the client sends '{"jsonrpc":"2.0","method":"foobar","id":"1"}'
    Then the server responds with error code -32601 and id "1"

  @FR-PROTO-06
  Scenario: Invalid params
    When the client sends '{"jsonrpc":"2.0","method":"subtract","params":"not-structured","id":1}'
    Then the server responds with error code -32602 and id 1

  @FR-PROTO-06
  Scenario: Parse error
    When the client sends the raw text '{"jsonrpc":"2.0","method":"foobar,"params":"bar","baz]'
    Then the server responds with error code -32700 and id null

  @FR-PROTO-06
  Scenario: Invalid request object
    When the client sends '{"jsonrpc":"2.0","method":1,"params":"bar"}'
    Then the server responds with error code -32600 and id null

  @FR-PROTO-06
  Scenario: Wrong protocol version is an invalid request
    When the client sends '{"jsonrpc":"1.0","method":"sum","params":[1,2],"id":1}'
    Then the server responds with error code -32600

  @FR-PROTO-06
  Scenario: Handler throws an unexpected exception
    Given the "sum" handler throws an unexpected exception
    When the client sends '{"jsonrpc":"2.0","method":"sum","params":[1,2],"id":1}'
    Then the server responds with error code -32603
    And the response does not leak the exception stack trace

  @FR-PROTO-06
  Scenario: Application error uses a code outside the reserved range
    Given the "sum" handler throws an application error with code 1001, message "Quota exceeded" and data {"limit":5}
    When the client sends '{"jsonrpc":"2.0","method":"sum","params":[1,2],"id":1}'
    Then the server responds with error code 1001, message "Quota exceeded" and data {"limit":5}

  @FR-PROTO-06
  Scenario: Application error code inside the reserved range is rejected at registration
    When a handler is configured to throw an application error with code -32050
    Then configuration fails with a message naming the reserved range -32768 to -32000

  @FR-PROTO-05
  Scenario: Batch of mixed calls and notifications
    When the client sends the batch
      """
      [
        {"jsonrpc":"2.0","method":"sum","params":[1,2,4],"id":"1"},
        {"jsonrpc":"2.0","method":"notify_hello","params":[7]},
        {"jsonrpc":"2.0","method":"subtract","params":[42,23],"id":"2"},
        {"foo":"boo"},
        {"jsonrpc":"2.0","method":"foo.get","params":{"name":"myself"},"id":"5"}
      ]
      """
    Then the server responds with a batch of 4 responses
    And the response for id "1" has result 7
    And the response for id "2" has result 19
    And one response has error code -32600 and id null
    And the response for id "5" has error code -32601

  @FR-PROTO-05
  Scenario: Empty batch is an invalid request
    When the client sends '[]'
    Then the server responds with a single error response with code -32600 and id null

  @FR-PROTO-05
  Scenario: Batch of only notifications returns nothing
    When the client sends the batch
      """
      [
        {"jsonrpc":"2.0","method":"notify_hello","params":[7]},
        {"jsonrpc":"2.0","method":"notify_hello","params":[8]}
      ]
      """
    Then the server sends no response

  @FR-PROTO-05
  Scenario: Invalid batch element does not fail the whole batch
    When the client sends '[1,2,3]'
    Then the server responds with a batch of 3 responses
    And every response has error code -32600 and id null

  @FR-PROTO-07
  Scenario: Reserved method namespace cannot be registered by applications
    When a handler is registered with the method name "rpc.custom"
    Then registration fails with a message that the "rpc." prefix is reserved
