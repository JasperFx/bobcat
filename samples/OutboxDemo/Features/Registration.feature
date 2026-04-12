Feature: Outbox Demo Registration

  Scenario: Submit a registration
    When I submit a registration for event "event-1" member "member-1" with payment 100
    Then the response status should be 204
    And the registration should be persisted in the database

  Scenario: Duplicate registration returns 409
    Given a registration for event "event-2" member "member-2" with payment 50 exists
    When I submit a registration for event "event-2" member "member-2" with payment 75
    Then the response status should be 409

  Scenario: Registration is persisted
    When I submit a registration for event "event-3" member "member-3" with payment 200
    Then the registration member id should be "member-3"
    And the registration event id should be "event-3"

  Scenario: Same member different event is allowed
    Given a registration for event "event-4" member "member-4" with payment 50 exists
    When I submit a registration for event "event-5" member "member-4" with payment 75
    Then the response status should be 204
