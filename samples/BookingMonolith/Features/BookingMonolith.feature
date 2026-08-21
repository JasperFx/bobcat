Feature: Booking Monolith

  The booking-modular-monolith conversion: four modules (Identity, Passenger, Flight, Booking)
  in one process. Identity and Passenger talk only over a durable local queue — registering a
  user makes the Passenger module create a passenger stub, and that is the one place the
  modules touch. Booking is event-sourced in Marten with an inline snapshot, so the booking
  read back below is the aggregate rebuilt from its stream, not a row the POST wrote.

  Scenario: Register a user
    When I register a user with email "booking.user@example.com" and password "Password1!"
    Then the response status is 201
    And the stored user has email "booking.user@example.com"

  Scenario: Registering a user creates a passenger stub
    When I register a user with email "stub.user@example.com" and password "Password1!"
    Then a passenger stub exists for that user

  Scenario: Register with bad email returns 400
    When I register a user with email "not-an-email" and password "Password1!"
    Then the response status is 400

  Scenario: Register with short password returns 400
    When I register a user with email "shortpwd@example.com" and password "abc"
    Then the response status is 400

  Scenario: Create a passenger
    When I create a passenger named "John Traveler" aged 30
    Then the response status is 201
    And the stored passenger is named "John Traveler" aged 30

  Scenario: Create passenger with empty name returns 400
    When I create a passenger named "" aged 25
    Then the response status is 400

  Scenario: Create a flight
    When I create flight "NYC-LAX" priced 299.99
    Then the response status is 201
    And the stored flight is "NYC-LAX" priced 299.99

  Scenario: Create flight with zero price returns 400
    When I create flight "BOS-SFO" priced 0.0
    Then the response status is 400

  Scenario: Get flights
    Given I create flight "ORD-MIA" priced 199.99
    When I get all flights
    Then the flights include "ORD-MIA"

  Scenario: Get flight by id
    Given I create flight "SEA-DEN" priced 149.99
    When I get the flight by id
    Then the response status is 200

  Scenario: Get flight by id returns 404 for missing
    When I get flight by id "00000000-0000-0000-0000-000000000000"
    Then the response status is 404

  Scenario: Create a booking
    Given I create a passenger named "Booker Passenger" aged 28
    And I create flight "ATL-PHX" priced 179.99
    When I book the flight for the passenger
    Then the response status is 201
    And the stored booking is for "Booker Passenger" on flight "ATL-PHX" priced 179.99

  Scenario: Booking for a passenger that does not exist returns 400
    Given I create flight "DTW-LAS" priced 159.99
    When I book the flight for a passenger that does not exist
    Then the response status is 400

  Scenario: Booking a flight that does not exist returns 400
    Given I create a passenger named "No Flight Passenger" aged 35
    When I book a flight that does not exist for the passenger
    Then the response status is 400

  Scenario: Get bookings
    Given I create a passenger named "Get Booker Passenger" aged 22
    And I create flight "CLT-SLC" priced 189.99
    And I book the flight for the passenger
    When I get all bookings
    Then the bookings include the new booking

  Scenario: Get booking by id
    Given I create a passenger named "ById Passenger" aged 40
    And I create flight "MSP-TPA" priced 210.00
    And I book the flight for the passenger
    When I get the booking by id
    Then the response status is 200
