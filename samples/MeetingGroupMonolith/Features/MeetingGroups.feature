Feature: Meeting Groups

  Scenario: Create a meeting group
    When I create a group named "Dotnet London" in "London"
    Then the response is 201 Created
    And the group name is "Dotnet London"
    And the group city is "London"

  Scenario: Get group details
    Given a group named "Rust Berlin" exists in "Berlin"
    When I get the group details
    Then the response is 200 OK
    And the group name is "Rust Berlin"

  Scenario: List all groups
    Given a group named "Go NYC" exists in "New York"
    When I list all groups
    Then the response is 200 OK
    And the group list has 1 group

  Scenario: Join a group as a member
    Given a group named "Python Paris" exists in "Paris"
    When I join the group as "Alice"
    Then the response is 201 Created

  Scenario: List group members
    Given a group named "Java Tokyo" exists in "Tokyo"
    And "Bob" joins the group
    When I list the group members
    Then the response is 200 OK
    And the member list has 1 member

  Scenario: Schedule an event for a group
    Given a group named "Kotlin Oslo" exists in "Oslo"
    When I schedule an event "Kotlin Intro" on "2025-09-15" with max 50 attendees
    Then the response is 201 Created
    And the event title is "Kotlin Intro"

  Scenario: List group events
    Given a group named "Swift Dublin" exists in "Dublin"
    And an event "Swift Meetup" exists on "2025-10-01" with max 30 attendees
    When I list group events
    Then the response is 200 OK
    And the event list has 1 event

  Scenario: Attend an event
    Given a group named "Elixir Vienna" exists in "Vienna"
    And an event "Elixir Workshop" exists on "2025-11-01" with max 20 attendees
    When I attend the event
    Then the response is 200 OK

  Scenario: Cannot attend a full event
    Given a group named "Scala Madrid" exists in "Madrid"
    And an event "Scala Conf" exists on "2025-12-01" with max 1 attendees
    And "Carol" joins the group
    When I attend the event
    And I try to attend the full event
    Then the response is 400 Bad Request

  Scenario: Multiple members join a group
    Given a group named "Haskell Amsterdam" exists in "Amsterdam"
    And "Dave" joins the group
    And "Eve" joins the group
    When I list the group members
    Then the member list has 2 members
