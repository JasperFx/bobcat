Feature: Project Management

  Scenario: Create a new project
    When I create a project titled "Clean the house" with admin "jeremy@jasperfx.net"
    Then the response status should be 201
