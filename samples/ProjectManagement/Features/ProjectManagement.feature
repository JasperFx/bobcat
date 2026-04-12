Feature: Project Management

  Scenario: Create a project returns 201
    When I create a project named "My Project" with description "A test project"
    Then the response status is 201
    And the project id is returned
