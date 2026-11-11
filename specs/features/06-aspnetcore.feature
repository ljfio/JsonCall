@phase2
Feature: ASP.NET Core hosting
  As a service author
  I want to add JSON-RPC to an ASP.NET Core app as an endpoint
  So that I get DI, authentication and middleware for free

  Background:
    Given an ASP.NET Core test host with services.AddJsonRpc(o => o.AddContract<IMath, MathService>())

  @FR-HOST-01
  Scenario: HTTP POST single request
    Given the app maps app.MapJsonRpc("/rpc")
    When a client POSTs '{"jsonrpc":"2.0","method":"add","params":[1,2],"id":1}' to "/rpc" as application/json
    Then the HTTP status is 200
    And the body is '{"jsonrpc":"2.0","result":3,"id":1}'

  @FR-HOST-01
  Scenario: HTTP POST of a notification returns no content
    Given the app maps app.MapJsonRpc("/rpc")
    When a client POSTs a notification to "/rpc"
    Then the HTTP status is 204

  @FR-HOST-01
  Scenario: HTTP POST of a batch
    Given the app maps app.MapJsonRpc("/rpc")
    When a client POSTs a batch of 2 calls
    Then the body is a batch of 2 responses

  @FR-HOST-01
  Scenario: Protocol errors still return HTTP 200 with a JSON-RPC error body
    Given the app maps app.MapJsonRpc("/rpc")
    When a client POSTs malformed JSON
    Then the HTTP status is 200
    And the body is a JSON-RPC error with code -32700

  @FR-HOST-01
  Scenario: WebSocket upgrade on the same route
    Given the app maps app.MapJsonRpc("/rpc")
    When a client opens a WebSocket to "/rpc" and sends a call
    Then it receives the response over the socket
    And the connection stays open for further calls

  @FR-HOST-01
  Scenario: Only the configured transports are exposed
    Given the app maps app.MapJsonRpc("/rpc", o => o.Transports = JsonRpcTransports.Http)
    When a client attempts a WebSocket upgrade on "/rpc"
    Then the upgrade is refused with HTTP 400 or 404

  @FR-HOST-01
  Scenario: Unsupported content type
    Given the app maps app.MapJsonRpc("/rpc")
    When a client POSTs with content type "text/plain"
    Then the HTTP status is 415

  @FR-HOST-01
  Scenario: Binary serializers are negotiated by content type
    Given the CBOR serializer is registered
    When a client POSTs a CBOR-encoded call with content type "application/cbor"
    Then the response is CBOR encoded with the same content type

  @FR-HOST-01
  Scenario: Endpoint conventions apply
    Given the app maps app.MapJsonRpc("/rpc").RequireAuthorization()
    When an unauthenticated client POSTs a call
    Then the HTTP status is 401

  @FR-HOST-04
  Scenario: Authorization attributes on a contract method
    Given the contract method "admin_reset" is annotated [Authorize(Roles = "admin")]
    When a non-admin user calls "admin_reset"
    Then the response is a JSON-RPC error with the documented "forbidden" code
    And the handler is not invoked

  @FR-HOST-01
  Scenario: CORS and rate limiting policies apply to the endpoint
    Given the app maps app.MapJsonRpc("/rpc").RequireCors("web").RequireRateLimiting("rpc")
    When a client from an allowed origin exceeds the rate limit
    Then it receives HTTP 429

  @FR-HOST-02
  Scenario: Handlers are resolved per call from DI
    Given MathService depends on a scoped service ICallContext
    When two calls arrive concurrently
    Then each call receives its own ICallContext instance

  @FR-HOST-02
  Scenario: WebSocket messages each get their own scope
    Given a WebSocket connection sending 3 calls
    Then each handler invocation has a distinct scope
    And scoped services are disposed after each call

  @FR-HOST-02
  Scenario: Handler can access the HttpContext and caller identity
    Given a handler that injects IHttpContextAccessor
    When an authenticated user calls it
    Then the handler sees that user's ClaimsPrincipal

  @FR-HOST-03
  Scenario: Duplicate method names fail at startup
    Given two contracts both declare the JSON-RPC method "add"
    When the application starts
    Then startup fails with an error naming both contracts and the method

  @FR-HOST-03
  Scenario: Contract implementation missing from DI
    Given a registered contract with no implementation
    When the application starts
    Then startup fails with an error naming the contract

  @FR-HOST-03
  Scenario: Method and parameter binding from the contract
    Given the contract method AddAsync(int a, int b)
    When called with params {"a":1,"b":2}, [1,2] and [1,2,"extra"]
    Then the first two succeed with 3 and the third fails with -32602

  @FR-HOST-03
  Scenario: Optional parameters use their default values
    Given the contract method GreetAsync(string name, string greeting = "Hello")
    When called with ["Ada"]
    Then the result is "Hello Ada"

  @FR-HOST-03
  Scenario: Cancellation token parameter is bound to the request
    Given a handler awaiting its CancellationToken
    When the HTTP client aborts the request
    Then the handler's token is cancelled

  @FR-HOST-05
  Scenario: Graceful shutdown completes in-flight calls
    Given a call to a handler that takes 500 ms is in flight
    When the host begins shutdown
    Then the call completes successfully before the host stops
    And new connections are refused

  @FR-HOST-05
  Scenario: Graceful shutdown ends open streams
    Given a client enumerating an endless stream
    When the host begins shutdown
    Then the stream is completed or terminated with a documented shutdown error
    And the producer's enumerator is disposed

  @FR-HOST-01
  Scenario: Minimal API style registration of a single method
    Given the app maps app.MapJsonRpc("/rpc", r => r.Method("echo", (string s) => s))
    When a client calls "echo" with ["hi"]
    Then the result is "hi"

  @FR-HOST-04
  Scenario: Endpoint is discoverable in endpoint metadata
    When the application's EndpointDataSource is enumerated
    Then an endpoint for "/rpc" exists with JSON-RPC metadata listing its method names
