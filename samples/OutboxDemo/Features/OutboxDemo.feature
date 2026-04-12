Feature: Outbox Demo

  Scenario: Submit event returns 204
    When I submit a member joined event for member "member-001" to group "group-001"
    Then the response status is 204

  Scenario: Duplicate event returns 409
    Given I submit a member joined event for member "dup-member" to group "dup-group"
    When I submit a member joined event for member "dup-member" to group "dup-group"
    Then the response status is 409

  Scenario: Event is persisted in Marten
    When I submit a member joined event for member "persist-member" to group "persist-group"
    Then the event is stored in the outbox

  Scenario: Same member different event type is allowed
    Given I submit a member joined event for member "multi-member" to group "multi-group"
    When I submit a member left event for member "multi-member" to group "multi-group"
    Then the response status is 204
