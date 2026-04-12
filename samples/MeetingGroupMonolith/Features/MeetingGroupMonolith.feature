Feature: Meeting Group Monolith

  Scenario: Register a user
    When I register a user with email "user@example.com" and password "Password1!"
    Then the response status is 200
    And the user id is returned

  Scenario: Register with invalid data returns 400
    When I register a user with empty email and password "Password1!"
    Then the response status is 400

  Scenario: Propose a meeting group
    Given I register a user with email "proposer@example.com" and password "Password1!"
    When I propose a meeting group named "DDD Group" in location "Warsaw"
    Then the response status is 201

  Scenario: Accept a meeting group proposal
    Given I register a user with email "accepter@example.com" and password "Password1!"
    And I propose a meeting group named "Clean Code Group" in location "Krakow"
    When I accept the meeting group proposal
    Then the response status is 200

  Scenario: Create a meeting
    Given I register a user with email "organizer@example.com" and password "Password1!"
    And I propose a meeting group named "Agile Group" in location "Gdansk"
    And I accept the meeting group proposal
    When I create a meeting named "First Meetup" in the group
    Then the response status is 201

  Scenario: Add an attendee
    Given I register a user with email "attendee@example.com" and password "Password1!"
    And I propose a meeting group named "DevOps Group" in location "Poznan"
    And I accept the meeting group proposal
    And I create a meeting named "DevOps Meetup" in the group
    When I add myself as an attendee
    Then the response status is 200

  Scenario: Get meeting groups
    Given I register a user with email "viewer@example.com" and password "Password1!"
    And I propose a meeting group named "Viewer Group" in location "Lodz"
    And I accept the meeting group proposal
    When I get all meeting groups
    Then at least 1 meeting group is returned

  Scenario: Get meetings
    Given I register a user with email "meeting.viewer@example.com" and password "Password1!"
    And I propose a meeting group named "Meeting Viewer Group" in location "Wroclaw"
    And I accept the meeting group proposal
    And I create a meeting named "Visible Meeting" in the group
    When I get all meetings for the group
    Then at least 1 meeting is returned

  Scenario: Create subscription
    Given I register a user with email "subscriber@example.com" and password "Password1!"
    When I create a subscription for 1 month
    Then the response status is 201

  Scenario: Create subscription with invalid period returns 400
    Given I register a user with email "badsub@example.com" and password "Password1!"
    When I create a subscription with period of 0 months
    Then the response status is 400
