@phase0
Feature: Specification harness smoke test
  As a maintainer
  I want a trivial executable specification
  So that I know the Gherkin harness and CI are wired up before real features land

  @smoke
  Scenario: Params by position serialize as a JSON array
    Given the default message serializer
    When the params "hello" and 1 are serialized by position
    Then the JSON is '["hello",1]'

  @smoke
  Scenario: Params by name serialize as a JSON object
    Given the default message serializer
    When the params {"hello": 1} are serialized by name
    Then the JSON is '{"hello":1}'
