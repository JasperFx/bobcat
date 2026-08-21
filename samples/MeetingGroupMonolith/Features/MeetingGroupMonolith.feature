Feature: Meeting Group Monolith

  The modular-monolith-with-ddd conversion: five modules in one process that talk to each other
  only through durable local queues. Registering a user makes the Meetings module create a member;
  accepting a proposal makes it create the group; starting a subscription in Payments pushes a new
  expiration date back onto the member in Meetings. None of those is a direct call, so the specs
  below are as much about the cascade arriving as about the endpoint returning.

  Scenario: Register a user
    When I register a user with email "user@example.com" and password "Password1!"
    Then the response status is 200
    And a member exists in the Meetings module for that user

  Scenario: Register with invalid data returns 400
    When I register a user with empty email and password "Password1!"
    Then the response status is 400

  Scenario: Propose a meeting group
    Given I register a user with email "proposer@example.com" and password "Password1!"
    When I propose a meeting group named "DDD Group" in "Warsaw", "PL"
    Then the response status is 201
    And the proposal is in verification

  Scenario: Propose a meeting group without a city returns 400
    Given I register a user with email "lost@example.com" and password "Password1!"
    When I propose a meeting group named "Nowhere Group" in "", "PL"
    Then the response status is 400

  Scenario: Accept a meeting group proposal
    Given I register a user with email "accepter@example.com" and password "Password1!"
    And I propose a meeting group named "Clean Code Group" in "Krakow", "PL"
    When I accept the meeting group proposal
    Then the response status is 200
    And the proposal is accepted
    And a meeting group named "Clean Code Group" exists for the proposal
    And the proposer is an organizer of the group

  Scenario: Create a meeting
    Given I register a user with email "organizer@example.com" and password "Password1!"
    And I propose a meeting group named "Agile Group" in "Gdansk", "PL"
    And I accept the meeting group proposal
    When I create a meeting named "First Meetup" in the group
    Then the response status is 201
    And the meeting "First Meetup" belongs to the group

  Scenario: Add an attendee
    Given I register a user with email "attendee@example.com" and password "Password1!"
    And I propose a meeting group named "DevOps Group" in "Poznan", "PL"
    And I accept the meeting group proposal
    And I create a meeting named "DevOps Meetup" in the group
    When I add myself as an attendee
    Then the response status is 200
    And the meeting has 1 attendee

  Scenario: Adding the same attendee twice returns 409
    Given I register a user with email "eager@example.com" and password "Password1!"
    And I propose a meeting group named "Eager Group" in "Katowice", "PL"
    And I accept the meeting group proposal
    And I create a meeting named "Eager Meetup" in the group
    And I add myself as an attendee
    When I add myself as an attendee
    Then the response status is 409
    And the meeting has 1 attendee

  Scenario: Get meeting groups
    Given I register a user with email "viewer@example.com" and password "Password1!"
    And I propose a meeting group named "Viewer Group" in "Lodz", "PL"
    And I accept the meeting group proposal
    When I get all meeting groups
    Then the meeting group "Viewer Group" is listed

  Scenario: Get meetings for a group
    Given I register a user with email "meeting.viewer@example.com" and password "Password1!"
    And I propose a meeting group named "Meeting Viewer Group" in "Wroclaw", "PL"
    And I accept the meeting group proposal
    And I create a meeting named "Visible Meeting" in the group
    When I get all meetings for the group
    Then the meeting "Visible Meeting" is listed

  Scenario: Create subscription
    Given I register a user with email "subscriber@example.com" and password "Password1!"
    When I create a Monthly subscription
    Then the response status is 201
    And the subscription is active
    And the member's subscription expiration is in the future

  Scenario: Create subscription with an unknown period returns 400
    Given I register a user with email "badsub@example.com" and password "Password1!"
    When I create a Weekly subscription
    Then the response status is 400
