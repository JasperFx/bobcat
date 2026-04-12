Feature: Meeting Group Monolith

  Scenario: Register a user
    When I register a user with login "jdoe" email "jdoe@example.com" and password "password123"
    Then the user id should be valid
    And the user login should be "jdoe"
    And the user email should be "jdoe@example.com"

  Scenario: Register user validates input
    When I try to register a user with invalid input
    Then the response status should be 400

  Scenario: Propose a meeting group
    When I propose a meeting group named "C# Enthusiasts" in "Portland"
    Then the proposal name should be "C# Enthusiasts"
    And the proposal status should be "InVerification"

  Scenario: Accept a proposal
    Given a meeting group proposal named "Rust Fans" in "Seattle" exists
    When I accept the proposal
    Then the proposal status should be "Accepted"
    And the proposal decision date should be set

  Scenario: Create a meeting
    Given an accepted meeting group named "Dev Group" in "Austin" exists
    When I create a meeting titled "Monthly Standup" in the group
    Then the meeting title should be "Monthly Standup"

  Scenario: Add attendee to a meeting
    Given an accepted meeting group named "Attendee Group" in "Denver" exists
    And a meeting titled "Test Meeting" exists in the group
    When I add an attendee to the meeting
    Then the meeting should have 1 attendee

  Scenario: Get meeting groups
    When I get all meeting groups
    Then the meeting groups list should be returned

  Scenario: Get meetings
    When I get all meetings
    Then the meetings list should be returned

  Scenario: Create a subscription
    When I create a subscription with period "Monthly"
    Then the subscription id should be valid

  Scenario: Create subscription validates period
    When I try to create a subscription with period "Invalid"
    Then the response status should be 400
