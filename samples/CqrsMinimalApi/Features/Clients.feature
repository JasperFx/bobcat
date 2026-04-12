Feature: Client Management

  Scenario: List clients when empty
    When I request the client list
    Then the response is 200 OK
    And the client list is empty

  Scenario: Create a client
    When I create a client with name "Acme Corp" and email "acme@example.com"
    Then the response is 201 Created
    And the client name is "Acme Corp"
    And the client email is "acme@example.com"

  Scenario: Get a client by id
    Given a client exists with name "BigCo" and email "bigco@example.com"
    When I get the client by id
    Then the response is 200 OK
    And the client name is "BigCo"

  Scenario: Update a client
    Given a client exists with name "OldName" and email "old@example.com"
    When I update the client name to "NewName" and email to "new@example.com"
    Then the response is 200 OK
    And the client name is "NewName"

  Scenario: Delete a client
    Given a client exists with name "ToDelete" and email "delete@example.com"
    When I delete the client
    Then the response is 204 No Content
