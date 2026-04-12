Feature: Booking Monolith

  # ── Identity ─────────────────────────────────────────────────────────────

  Scenario: Register user returns user account
    When I register a user with email "alice@test.com" first name "Alice" last name "Smith" and password "Password123"
    Then the user account id should be valid
    And the user account email should be "alice@test.com"
    And the user first name should be "Alice"
    And the user last name should be "Smith"

  Scenario: Register user rejects invalid email
    When I try to register a user with email "bad-email" and password "Password123"
    Then the response status should be 400

  Scenario: Register user rejects short password
    When I try to register a user with email "a@b.com" and password "short"
    Then the response status should be 400

  # ── Passenger ────────────────────────────────────────────────────────────

  Scenario: Create passenger returns passenger record
    When I create a passenger named "Bob Jones" with passport "AB123456" type "Male" and age 30
    Then the passenger id should be valid
    And the passenger name should be "Bob Jones"
    And the passenger passport should be "AB123456"
    And the passenger age should be 30

  Scenario: Create passenger rejects empty name
    When I try to create a passenger with empty name and passport "AB123456"
    Then the response status should be 400

  # ── Flight ───────────────────────────────────────────────────────────────

  Scenario: Create flight returns flight record
    When I create a flight with number "FL100" and price 299.99
    Then the flight id should be valid
    And the flight number should be "FL100"
    And the flight price should be 299.99

  Scenario: Create flight rejects zero price
    When I try to create a flight with number "FL100" and price 0
    Then the response status should be 400

  Scenario: Get flights returns list
    Given a flight with number "FL200" and price 199 exists
    When I get all flights
    Then the flights list should not be empty

  Scenario: Get flight by id returns flight
    Given a flight with number "FL300" and price 150 exists
    When I get the flight by id
    Then the flight number should be "FL300"

  Scenario: Get flight by id returns 404 for missing
    When I get a flight by a random id
    Then the response status should be 404

  # ── Booking ──────────────────────────────────────────────────────────────

  Scenario: Create booking with valid passenger and flight
    Given a passenger named "Test User" with passport "TP123456" exists
    And a flight with number "FL400" and price 500 exists
    When I create a booking for the passenger and flight with description "My booking"
    Then the booking id should be valid
    And the booking passenger id should match

  Scenario: Create booking returns 400 for missing passenger
    Given a flight with number "FL500" and price 250 exists
    When I try to create a booking with a non-existent passenger
    Then the response status should be 400

  Scenario: Create booking returns 400 for missing flight
    Given a passenger named "Test User2" with passport "TU234567" exists
    When I try to create a booking with a non-existent flight
    Then the response status should be 400

  Scenario: Get bookings returns list
    Given a passenger named "List Test" with passport "LT123456" exists
    And a flight with number "FL600" and price 100 exists
    And a booking for the passenger and flight exists
    When I get all bookings
    Then the bookings list should not be empty

  Scenario: Get booking by id returns booking
    Given a passenger named "ById Test" with passport "BI123456" exists
    And a flight with number "FL700" and price 350 exists
    And a booking for the passenger and flight with description "Test booking" exists
    When I get the booking by id
    Then the booking id should match

  Scenario: Create booking rejects empty passenger and flight ids
    When I try to create a booking with empty ids
    Then the response status should be 400
