@phase7
Feature: Broadcasting notifications to connected peers
  As a daemon author
  I want to track every attached connection and push notifications to all of them
  So that I can serve an event stream without hand-rolling a peer registry

  @FR-BCAST-01
  Scenario: A broadcast reaches every connected peer
    Given a connection set with 3 attached peers
    When the server broadcasts "tick" with value 7
    Then every peer receives "tick" with value 7

  @FR-BCAST-02
  Scenario: A disconnected peer leaves the set
    Given a connection set with 3 attached peers
    When 1 peer disconnects
    Then the connection set has 2 peers

  @FR-BCAST-03
  Scenario: A broadcast can be filtered
    Given a connection set with 3 attached peers
    When the server broadcasts "tick" with value 1 to the first peer only
    Then only the first peer receives "tick"
