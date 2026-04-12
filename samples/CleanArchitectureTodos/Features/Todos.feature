Feature: Clean Architecture Todos

  Scenario: Create a todo list
    When I create a todo list titled "Groceries" with colour "#FF0000"
    Then the todo list title should be "Groceries"
    And the todo list colour should be "#FF0000"

  Scenario: Create a todo list uses default colour
    When I create a todo list titled "Default Colour List" with no colour
    Then the todo list colour should be "#808080"

  Scenario: Create todo list with duplicate title returns 400
    Given a todo list titled "Duplicate" exists
    When I try to create a todo list titled "Duplicate" with no colour
    Then the response status should be 400

  Scenario: Update a todo list
    Given a todo list titled "Original" with colour "#0000FF" exists
    When I update the todo list title to "Updated" and colour to "#FF0000"
    Then the todo list title should be "Updated"
    And the todo list colour should be "#FF0000"

  Scenario: Update todo list with duplicate title returns 400
    Given a todo list titled "First" exists
    And a todo list titled "Second" exists
    When I try to update the second todo list title to "First"
    Then the response status should be 400

  Scenario: Delete a todo list
    Given a todo list titled "To Delete" exists
    When I delete the todo list
    Then the response status should be 204

  Scenario: Get all todo lists
    Given a todo list titled "List A" exists
    And a todo list titled "List B" exists
    When I get all todo lists
    Then there should be at least 2 todo lists
    And the result should contain priority levels
    And the result should contain colours

  Scenario: Create a todo item
    Given a todo list titled "Items List" exists
    When I create a todo item titled "Buy milk" in the list
    Then the todo item title should be "Buy milk"
    And the todo item should not be done

  Scenario: Update a todo item
    Given a todo list titled "Update Item List" exists
    And a todo item titled "Original Item" exists in the list
    When I update the todo item title to "Updated Item" and mark it done
    Then the response status should be 204

  Scenario: Update todo item detail
    Given a todo list titled "Detail List" exists
    And a todo item titled "Detail Item" exists in the list
    When I update the todo item detail with priority "High" and note "Important note"
    Then the response status should be 204

  Scenario: Delete a todo item
    Given a todo list titled "Delete Item List" exists
    And a todo item titled "To Remove" exists in the list
    When I delete the todo item
    Then the response status should be 204
