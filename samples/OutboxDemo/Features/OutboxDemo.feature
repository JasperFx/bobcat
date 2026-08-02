Feature: Outbox Demo

  The sample exposes one endpoint, POST /registration. Its happy path returns 204 while
  cascading a Registration saga and two messages through Marten's outbox in the same
  transaction; its compound ValidateAsync handler rejects a duplicate registration with 409.

  Scenario: A new registration is accepted
    When I submit a registration for member "member-001" at event "event-001"
    Then the response status is 204

  Scenario: The same member cannot register twice for one event
    Given a registration for member "dup-member" at event "dup-event"
    When I submit a registration for member "dup-member" at event "dup-event"
    Then the response status is 409
    And the rejection names the duplicate member "dup-member"

  Scenario: The same member may register for a different event
    Given a registration for member "multi-member" at event "event-a"
    When I submit a registration for member "multi-member" at event "event-b"
    Then the response status is 204

  Scenario: The payment rides along with the registration
    When I submit a registration for member "paying-member" at event "event-002" paying 250
    Then the response status is 204
