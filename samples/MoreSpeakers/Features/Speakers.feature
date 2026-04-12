Feature: Speaker Management

  Scenario: List speakers when empty
    When I request the speaker list
    Then the response is 200 OK
    And the speaker list is empty

  Scenario: Add a speaker
    When I add a speaker with name "Jane Doe" bio "Expert in distributed systems" and topic "Microservices"
    Then the response is 201 Created
    And the speaker name is "Jane Doe"
    And the speaker topic is "Microservices"

  Scenario: Get a speaker by id
    Given a speaker exists with name "John Smith" bio "Cloud architect" and topic "Azure"
    When I get the speaker by id
    Then the response is 200 OK
    And the speaker name is "John Smith"

  Scenario: Update speaker bio
    Given a speaker exists with name "Alice Brown" bio "Old bio" and topic "DevOps"
    When I update the speaker bio to "New bio about CI/CD pipelines"
    Then the response is 200 OK
    And the speaker bio is "New bio about CI/CD pipelines"

  Scenario: Delete a speaker
    Given a speaker exists with name "Bob Wilson" bio "Some bio" and topic "Testing"
    When I delete the speaker
    Then the response is 204 No Content

  Scenario: Add a session to a speaker
    Given a speaker exists with name "Carol White" bio "Performance expert" and topic "Performance"
    When I add a session with title "Optimizing .NET Apps" and duration 45 minutes
    Then the response is 201 Created
    And the session title is "Optimizing .NET Apps"
    And the session duration is 45 minutes

  Scenario: List sessions for a speaker
    Given a speaker exists with name "Dave Green" bio "Security researcher" and topic "Security"
    And a session exists with title "Zero Trust Architecture" and duration 60 minutes
    When I get the sessions for the speaker
    Then the response is 200 OK
    And the session list has 1 items

  Scenario: Speaker with multiple sessions
    Given a speaker exists with name "Eve Black" bio "Full stack developer" and topic "Full Stack"
    And a session exists with title "Frontend Tips" and duration 30 minutes
    And a session exists with title "Backend Patterns" and duration 45 minutes
    When I get the sessions for the speaker
    Then the response is 200 OK
    And the session list has 2 items

  Scenario: Get sessions for non-existent speaker returns 404
    When I get sessions for a non-existent speaker with id 9999
    Then the response is 404 Not Found

  Scenario: Update a speaker topic
    Given a speaker exists with name "Frank Blue" bio "Veteran coder" and topic "Old Topic"
    When I update the speaker topic to "Modern .NET"
    Then the response is 200 OK
    And the speaker topic is "Modern .NET"
