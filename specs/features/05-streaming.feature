@phase3
Feature: Streaming responses with IAsyncEnumerable
  As a service author
  I want a method to return IAsyncEnumerable<T> and have items reach the client as they are produced
  So that I can push events and large result sets without buffering

  # The wire convention for streams is a DRAFT (PRD decision D2).
  # Scenarios whose steps mention "$/stream" or SSE event names are tagged @draft.

  Background:
    Given a server contract
      """
      [JsonRpcContract]
      public interface IEvents
      {
          IAsyncEnumerable<Tick> TicksAsync(int count, int delayMs, CancellationToken ct);
          IAsyncEnumerable<Tick> FailAfterAsync(int items, CancellationToken ct);
          IAsyncEnumerable<Tick> SubscribeAsync(string topic, CancellationToken ct);
      }
      """

  @FR-STREAM-01
  Scenario Outline: Items arrive as produced, not buffered
    Given a client connected over <transport>
    When the client calls TicksAsync(5, 100) and enumerates the result
    Then the first item is received before the producer has yielded the third
    And all 5 items arrive in order
    And the enumeration completes

    Examples:
      | transport |
      | http-sse  |
      | websocket |
      | tcp       |
      | stdio     |

  @FR-STREAM-02 @draft
  Scenario: HTTP stream is delivered as Server-Sent Events
    When a raw HTTP client POSTs a TicksAsync(3, 0) request with "Accept: text/event-stream"
    Then the response content type is "text/event-stream"
    And the body contains 3 "data:" events with the item JSON
    And the last event is "event: complete"

  @FR-STREAM-02
  Scenario: Client that does not accept SSE gets a clear error for a streaming method
    When a raw HTTP client POSTs a TicksAsync(3, 0) request with "Accept: application/json"
    Then the response is a JSON-RPC error explaining the method streams
    And the HTTP status is 406 or the error code is the documented "stream required" code

  @FR-STREAM-03 @draft
  Scenario: Message transports use stream notifications
    Given a client connected over websocket
    When a raw peer sends a TicksAsync(2, 0) call with id 1
    Then it first receives the response {"id":1,"result":{"stream":"<streamId>"}}
    And then two "$/stream.item" notifications with seq 0 and 1 and the item values
    And then a "$/stream.end" notification for that stream

  @FR-STREAM-03 @draft
  Scenario: Items of different streams on one connection do not interleave incorrectly
    Given a client connected over websocket
    When two TicksAsync streams are enumerated concurrently
    Then each enumeration yields only its own items in order

  @FR-STREAM-04
  Scenario Outline: Client cancellation stops the producer
    Given a client connected over <transport>
    And a producer that yields forever and records when its enumerator is disposed
    When the client enumerates 3 items and then cancels
    Then the producer's cancellation token is signalled within 500 ms
    And the producer's enumerator is disposed exactly once
    And no further items are sent

    Examples:
      | transport |
      | http-sse  |
      | websocket |
      | tcp       |

  @FR-STREAM-04
  Scenario Outline: Disconnect stops the producer and releases the subscription
    Given a client connected over <transport>
    And SubscribeAsync("orders") registers a subscription
    When the client connection drops abruptly
    Then the subscription is unsubscribed
    And the producer's enumerator is disposed

    Examples:
      | transport |
      | http-sse  |
      | websocket |

  @FR-STREAM-04
  Scenario: Subscription is released even if the response write throws
    Given SubscribeAsync("orders") registers a subscription
    And the response stream fails on the second write
    Then the subscription is unsubscribed

  @FR-STREAM-05
  Scenario Outline: Exception mid-stream terminates with a JSON-RPC error
    Given a client connected over <transport>
    When the client enumerates FailAfterAsync(2)
    Then it receives 2 items
    And then a JsonRpcException is thrown from the enumeration
    And the items already received are unaffected

    Examples:
      | transport |
      | http-sse  |
      | websocket |

  @FR-STREAM-05
  Scenario: Exception before the first item
    When the client enumerates FailAfterAsync(0)
    Then a JsonRpcException is thrown on the first MoveNextAsync

  @FR-STREAM-06
  Scenario: Slow consumer applies back-pressure
    Given a producer yielding 10,000 items
    And a consumer that processes one item per 10 ms
    When the client enumerates the stream
    Then server-side buffered items never exceed the configured stream buffer size
    And the producer is paused while the buffer is full

  @FR-STREAM-06
  Scenario: Stream can be abandoned by disposing the enumerator
    When the client enumerates 2 items and disposes the enumerator
    Then the server stops the producer

  @FR-STREAM-01
  Scenario: Empty stream
    When the client enumerates TicksAsync(0, 0)
    Then the enumeration completes with no items and no error

  @FR-STREAM-01
  Scenario: Stream method called as a notification is rejected
    When a raw peer sends TicksAsync as a notification without an id
    Then no stream is started

  @FR-STREAM-01
  Scenario: Authorization applies before any item is produced
    Given the TicksAsync method requires the "reader" role
    When an unauthorized client calls it
    Then no producer is started
    And the client receives an authorization error

  @FR-STREAM-01
  Scenario: Streaming results appear in the OpenRPC document
    When the OpenRPC document is generated
    Then the TicksAsync method has "x-jsoncall-stream": true
    And its result schema describes a single Tick item
