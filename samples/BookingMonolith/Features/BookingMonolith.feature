Feature: Booking Monolith

  Scenario: Register a user
    When I register a user with email "booking.user@example.com" and password "Password1!"
    Then the response status is 201
    And the user id is returned

  Scenario: Register with bad email returns 400
    When I register a user with email "not-an-email" and password "Password1!"
    Then the response status is 400

  Scenario: Register with short password returns 400
    When I register a user with email "shortpwd@example.com" and password "abc"
    Then the response status is 400

  Scenario: Create a passenger
    Given I register a user with email "passenger@example.com" and password "Password1!"
    When I create a passenger with name "John Traveler" and age 30
    Then the response status is 201
    And the passenger id is returned

  Scenario: Create passenger with empty name returns 400
    Given I register a user with email "badpassenger@example.com" and password "Password1!"
    When I create a passenger with name "" and age 25
    Then the response status is 400

  Scenario: Create a flight
    When I create a flight from "NYC" to "LAX" with price 299.99
    Then the response status is 201
    And the flight id is returned

  Scenario: Create flight with zero price returns 400
    When I create a flight from "BOS" to "SFO" with price 0.0
    Then the response status is 400

  Scenario: Get flights
    Given I create a flight from "ORD" to "MIA" with price 199.99
    When I get all flights
    Then at least 1 flight is returned

  Scenario: Get flight by id
    Given I create a flight from "SEA" to "DEN" with price 149.99
    When I get the flight by id
    Then the response status is 200

  Scenario: Get flight by id returns 404 for missing
    When I get flight by id "00000000-0000-0000-0000-000000000000"
    Then the response status is 404

  Scenario: Create a booking
    Given I register a user with email "booker@example.com" and password "Password1!"
    And I create a passenger with name "Booker Passenger" and age 28
    And I create a flight from "ATL" to "PHX" with price 179.99
    When I create a booking
    Then the response status is 201

  Scenario: Create booking missing passenger returns 400
    Given I register a user with email "nopassenger@example.com" and password "Password1!"
    And I create a flight from "DTW" to "LAS" with price 159.99
    When I create a booking with missing passenger
    Then the response status is 400

  Scenario: Create booking missing flight returns 400
    Given I register a user with email "noflight@example.com" and password "Password1!"
    And I create a passenger with name "No Flight Passenger" and age 35
    When I create a booking with missing flight
    Then the response status is 400

  Scenario: Get bookings
    Given I register a user with email "get.booker@example.com" and password "Password1!"
    And I create a passenger with name "Get Booker Passenger" and age 22
    And I create a flight from "CLT" to "SLC" with price 189.99
    And I create a booking
    When I get all bookings
    Then at least 1 booking is returned

  Scenario: Get booking by id
    Given I register a user with email "booking.byid@example.com" and password "Password1!"
    And I create a passenger with name "ById Passenger" and age 40
    And I create a flight from "MSP" to "TPA" with price 210.00
    And I create a booking
    When I get the booking by id
    Then the response status is 200
