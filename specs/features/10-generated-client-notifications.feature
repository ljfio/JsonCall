@phase7
Feature: Typed handlers for server-pushed notifications in generated clients
  As an integrator of an event-driven protocol
  I want the generated client to expose typed On<Name> handlers for notifications the server pushes
  So that I subscribe to events with typed callbacks instead of raw peer.OnNotification calls

  # A result-less method marked "x-jsoncall-direction": "server-to-client" is a push.
  # Without the marker a result-less method is a call that sends a notification (EventAsync is not a subscription).
  # The scenarios generate clients from the checked-in Language Server Protocol and Agent Client Protocol contracts
  # (test/JsonCall.Specs/Support/Data/lsp and .../acp), whose types live in sibling schema files. Requests from the
  # server to the client (for example ACP "fs/read_text_file") are not covered: they need a handler, not a subscription.

  Background:
    Given a project referencing the JsonCall client generator

  # ---------------------------------------------------------------- Language Server Protocol

  @FR-GEN-03 @FR-GEN-07 @lsp
  Scenario: LSP window/logMessage is delivered as typed arguments
    Given the Language Server Protocol contract
    When the generated client registers OnWindowLogMessage
    And the server pushes "window/logMessage" with params {"type":3,"message":"indexed 42 files"}
    Then the handler receives [3,"indexed 42 files"]

  @FR-GEN-03 @FR-GEN-07 @lsp
  Scenario: LSP textDocument/publishDiagnostics delivers typed diagnostics from a sibling schema
    Given the Language Server Protocol contract
    When the generated client registers OnTextDocumentPublishDiagnostics
    And the server pushes "textDocument/publishDiagnostics" with params {"uri":"file:///src/main.rs","version":4,"diagnostics":[{"range":{"start":{"line":3,"character":8},"end":{"line":3,"character":9}},"severity":2,"source":"rustc","message":"unused variable: `x`"}]}
    Then the handler receives ["file:///src/main.rs",4,[{"range":{"start":{"line":3,"character":8},"end":{"line":3,"character":9}},"severity":2,"source":"rustc","message":"unused variable: `x`"}]]

  @FR-GEN-03 @FR-GEN-07 @lsp
  Scenario: LSP optional params that are omitted arrive as null
    Given the Language Server Protocol contract
    When the generated client registers OnTextDocumentPublishDiagnostics
    And the server pushes "textDocument/publishDiagnostics" with params {"uri":"file:///src/main.rs","diagnostics":[]}
    Then the handler receives ["file:///src/main.rs",null,[]]

  @FR-GEN-03 @FR-GEN-07 @lsp
  Scenario: LSP $/progress is exposed under a readable name and keeps its union type
    Given the Language Server Protocol contract
    When the generated client registers OnProgress
    And the server pushes "$/progress" with params {"token":"indexing","value":{"kind":"report","message":"12/30 crates","percentage":40}}
    Then the handler receives ["indexing",{"kind":"report","message":"12/30 crates","percentage":40}]
    And argument 2 is a "WorkDoneProgressReport"

  @FR-GEN-03 @FR-GEN-07 @lsp
  Scenario: LSP client-to-server notifications stay calls
    Given the Language Server Protocol contract
    Then the interface has "TextDocumentDidOpenAsync" and no "OnTextDocumentDidOpen"

  # ---------------------------------------------------------------- Agent Client Protocol

  @FR-GEN-03 @FR-GEN-07 @acp
  Scenario: ACP session/update delivers the session id and a typed update
    Given the Agent Client Protocol contract
    When the generated client registers OnSessionUpdate
    And the server pushes "session/update" with params {"sessionId":"sess_1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Reading the file"}}}
    Then the handler receives ["sess_1",{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Reading the file"}}]
    And argument 2 is a "SessionUpdateAgentMessageChunk"

  @FR-GEN-03 @FR-GEN-07 @acp
  Scenario: ACP session/update tool calls arrive as their own variant
    Given the Agent Client Protocol contract
    When the generated client registers OnSessionUpdate
    And the server pushes "session/update" with params {"sessionId":"sess_1","update":{"sessionUpdate":"tool_call","toolCallId":"call_1","title":"Reading configuration file","kind":"read","status":"pending"}}
    Then argument 2 is a "SessionUpdateToolCall"

  @FR-GEN-03 @FR-GEN-07 @acp
  Scenario: ACP session/cancel is a call that sends a notification
    Given the Agent Client Protocol contract
    When the generated client calls SessionCancelAsync with "sess_1"
    Then the server receives the notification "session/cancel" for "sess_1"
    And the interface has "SessionCancelAsync" and no "OnSessionCancel"

  # ---------------------------------------------------------------- shared behavior

  @FR-GEN-03 @FR-GEN-07
  Scenario: Pushes with positional params are delivered too
    Given the Language Server Protocol contract
    When the generated client registers OnWindowLogMessage
    And the server pushes "window/logMessage" with params [2,"warming up"]
    Then the handler receives [2,"warming up"]

  @FR-GEN-03 @FR-GEN-07
  Scenario: Registering a handler again replaces the previous one
    Given the Language Server Protocol contract
    When the generated client registers OnWindowLogMessage twice
    And the server pushes "window/logMessage" with params {"type":3,"message":"once"}
    Then only the second handler runs

  @FR-GEN-03 @FR-GEN-07
  Scenario: Push handlers do not appear on the call interface
    Given the Agent Client Protocol contract
    Then the interface has no member starting with "On"
    And the generated code compiles without warnings
