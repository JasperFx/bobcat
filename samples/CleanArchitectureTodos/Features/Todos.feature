Feature: Todo Management

  Scenario: Create a todo
    When I create a todo with title "Buy groceries"
    Then the response is 201 Created
    And the todo title is "Buy groceries"

  Scenario: Get a todo by id
    Given a todo exists with title "Read a book"
    When I get the todo by id
    Then the response is 200 OK
    And the todo title is "Read a book"

  Scenario: List all todos
    Given a todo exists with title "Task One"
    Given a todo exists with title "Task Two"
    When I list all todos
    Then the response is 200 OK
    And the todo list has 2 items

  Scenario: Complete a todo
    Given a todo exists with title "Exercise"
    When I mark the todo as complete
    Then the response is 200 OK
    And the todo is completed

  Scenario: Delete a todo
    Given a todo exists with title "Old Task"
    When I delete the todo
    Then the response is 204 No Content

  Scenario: Create todo with description
    When I create a todo with title "Write tests" and description "Cover all edge cases"
    Then the response is 201 Created
    And the todo title is "Write tests"
    And the todo description is "Cover all edge cases"

  Scenario: Update todo title
    Given a todo exists with title "Original Title"
    When I update the todo title to "Updated Title"
    Then the response is 200 OK
    And the todo title is "Updated Title"

  Scenario: List only completed todos
    Given a todo exists with title "Done Task"
    When I mark the todo as complete
    Given a todo exists with title "Pending Task"
    When I list only completed todos
    Then the response is 200 OK
    And the todo list has 1 items
    And all listed todos are completed

  Scenario: Get a non-existent todo returns 404
    When I get a todo that does not exist
    Then the response is 404 Not Found

  Scenario: Delete a non-existent todo returns 404
    When I delete a todo that does not exist
    Then the response is 404 Not Found
