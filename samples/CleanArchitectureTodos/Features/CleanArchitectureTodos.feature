Feature: Clean Architecture Todos

  Scenario: Create a todo list
    When I create a todo list with title "My List"
    Then the response status is 201
    And the list id is returned

  Scenario: New todo list has default colour
    When I create a todo list with title "Colour List"
    Then the list has the default colour

  Scenario: Create duplicate title returns 400
    Given I create a todo list with title "Duplicate"
    When I create a todo list with title "Duplicate"
    Then the response status is 400

  Scenario: Update a todo list
    Given I create a todo list with title "Old Title"
    When I update the list title to "New Title"
    Then the response status is 200

  Scenario: Update to duplicate title returns 400
    Given I create a todo list with title "First"
    And I create a todo list with title "Second"
    When I update the list "Second" title to "First"
    Then the response status is 400

  Scenario: Delete a todo list
    Given I create a todo list with title "To Delete"
    When I delete the todo list
    Then the response status is 204

  Scenario: Get all todo lists
    Given I create a todo list with title "List A"
    And I create a todo list with title "List B"
    When I get all todo lists
    Then at least 2 lists are returned

  Scenario: Create a todo item
    Given I create a todo list with title "Item List"
    When I create a todo item with title "Do something"
    Then the response status is 201

  Scenario: Update a todo item
    Given I create a todo list with title "Update Item List"
    And I create a todo item with title "Original"
    When I update the todo item title to "Updated"
    Then the response status is 200

  Scenario: Delete a todo item
    Given I create a todo list with title "Delete Item List"
    And I create a todo item with title "To Delete"
    When I delete the todo item
    Then the response status is 204
