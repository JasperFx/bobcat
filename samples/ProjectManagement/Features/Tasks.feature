Feature: Project Task Management

  Scenario: Create a task via Wolverine and verify via HTTP
    When I dispatch a CreateTask command for project "Alpha" title "Build API" assigned to "Alice"
    Then the task list contains 1 task
    And the task title is "Build API"
    And the task is assigned to "Alice"
