Feature: CQRS Minimal API Students

  Scenario: Create a student
    When I create a student with name "Alice Smith" and email "alice@school.com"
    Then the response status is 201
    And the student id is returned

  Scenario: Get all students ordered
    Given I create a student with name "Zara" and email "zara@school.com"
    And I create a student with name "Aaron" and email "aaron@school.com"
    When I get all students
    Then at least 2 students are returned
    And they are ordered by name

  Scenario: Get student by id returns 404 for missing
    When I get student by id "00000000-0000-0000-0000-000000000000"
    Then the response status is 404

  Scenario: Update a student
    Given I create a student with name "Bob Jones" and email "bob@school.com"
    When I update the student name to "Robert Jones"
    Then the response status is 200

  Scenario: Delete student and verify 404
    Given I create a student with name "Carol White" and email "carol@school.com"
    When I delete the student
    Then the response status is 204
    When I get the student by id
    Then the response status is 404
