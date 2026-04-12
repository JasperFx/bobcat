Feature: CQRS Minimal API Students

  Scenario: Create a student
    When I create a student named "Alice Test" with address "100 Test Blvd" and email "alice@test.com"
    Then the student name should be "Alice Test"
    And the student id should be valid

  Scenario: Get all students
    Given a student named "Zara First" exists
    And a student named "Amy Second" exists
    When I get all students
    Then there should be at least 2 students
    And the first student should be "Amy Second"

  Scenario: Get by id returns 404 for missing student
    When I get a student by id 999999
    Then the response status should be 404

  Scenario: Update a student
    Given a student named "Original Name" with email "orig@test.com" exists
    When I update the student name to "Updated Name" and email to "updated@test.com"
    Then the updated student name should be "Updated Name"
    And the updated student email should be "updated@test.com"

  Scenario: Delete a student
    Given a student named "To Delete" with email "del@test.com" exists
    When I delete the student
    And I get the student by its id
    Then the response status should be 404
