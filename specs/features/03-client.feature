@phase1 @phase2
Feature: Correlating, typed client
  As an application developer
  I want to make concurrent typed calls over one connection
  So that I can use remote methods like local ones

  Background:
    Given a connected client and a server with the methods "delay_echo", "fail", "notify_me" and "add"

  @FR-CLIENT-01
  Scenario: Responses arriving out of order are matched to the right call
    Given "delay_echo" with ["first"] takes 200 ms and "delay_echo" with ["second"] takes 10 ms
    When the client issues both calls concurrently
    Then the first call returns "first"
    And the second call returns "second"

  @FR-CLIENT-01
  Scenario: Many concurrent calls on one connection
    When the client issues 1000 concurrent "add" calls with distinct arguments
    Then every call returns the sum of its own arguments
    And no call receives another call's response

  @FR-PROTO-02
  Scenario: Client ids are unique per in-flight call
    When the client issues 100 concurrent calls
    Then all request ids on the wire are distinct

  @FR-CLIENT-02
  Scenario: Call times out
    Given the default call timeout is 100 ms
    When the client calls "delay_echo" which takes 1 second
    Then the call fails with a TimeoutException
    And the connection remains usable for further calls

  @FR-CLIENT-02
  Scenario: Late response after a timeout is discarded
    Given the default call timeout is 100 ms
    And the client calls "delay_echo" which takes 300 ms and times out
    When the late response arrives
    Then it is ignored without error
    And a subsequent call returns its own result

  @FR-CLIENT-02
  Scenario: Caller cancels a call
    Given a call to "delay_echo" which takes 5 seconds is in flight
    When the caller cancels its token
    Then the call fails with an OperationCanceledException within 100 ms
    And the server is notified of the cancellation

  @FR-CLIENT-02
  Scenario: Per-call timeout overrides the default
    Given the default call timeout is 30 seconds
    When the client calls "delay_echo" with a 50 ms timeout and the call takes 1 second
    Then the call fails with a TimeoutException

  @FR-CLIENT-03
  Scenario: Server notification is delivered to a handler
    Given the client has a handler for the notification "progress"
    When the server sends the notification "progress" with {"percent":50}
    Then the handler is invoked once with {"percent":50}

  @FR-CLIENT-03
  Scenario: Server notification during an in-flight call does not disturb it
    Given a call to "delay_echo" is in flight
    When the server sends 10 notifications and then the response
    Then the call completes with its own result
    And the handler received all 10 notifications

  @FR-CLIENT-03
  Scenario: Notification with no registered handler is ignored
    When the server sends the notification "unhandled"
    Then the client logs it at debug level and continues

  @FR-CLIENT-03
  Scenario: Server-to-client call
    Given the client exposes a method "ping" returning "pong"
    When the server calls "ping" on the client
    Then the server receives "pong"

  @FR-CLIENT-04
  Scenario: Batch call returns typed results per entry
    When the client sends a batch of "add"[1,2], "add"[3,4] and "fail"
    Then results 3 and 7 are returned in request order
    And the "fail" entry carries a JsonRpcException with the server's error

  @FR-CLIENT-04
  Scenario: Batch containing notifications
    When the client sends a batch of one call and two notifications
    Then one result is returned

  @FR-CLIENT-05
  Scenario: Server error becomes a typed exception
    When the client calls "fail"
    Then a JsonRpcException is thrown with the server's code, message and data

  @FR-CLIENT-05
  Scenario: Calling after disposal
    Given the client has been disposed
    When the client calls "add" with [1,2]
    Then an ObjectDisposedException is thrown

  @FR-CLIENT-06 @phase2
  Scenario: Typed proxy from a contract interface
    Given the contract
      """
      [JsonRpcContract]
      public interface IMath
      {
          Task<int> AddAsync(int a, int b, CancellationToken ct = default);
          [JsonRpcMethod("sub")] Task<int> SubtractAsync(int minuend, int subtrahend);
          [JsonRpcNotification] Task LogAsync(string message);
      }
      """
    When a proxy is created over the connected client
    And the proxy calls AddAsync(2, 3)
    Then the wire request is a call to "add" with params by position or name per the contract setting
    And the result is 5

  @FR-CLIENT-06 @phase2
  Scenario: Proxy notification sends no id and awaits only the send
    When the proxy calls LogAsync("hi")
    Then the wire message has no id
    And the task completes without waiting for a response

  @FR-CLIENT-06 @phase2
  Scenario: Proxy cancellation token parameter is not serialized
    When the proxy calls AddAsync(2, 3) with a cancellation token
    Then the wire params contain only the arguments 2 and 3

  @FR-CLIENT-06 @phase2
  Scenario: Proxy maps JSON-RPC errors to exceptions
    Given the server returns error code 1001 for "add"
    When the proxy calls AddAsync(1, 1)
    Then a JsonRpcException with code 1001 is thrown
