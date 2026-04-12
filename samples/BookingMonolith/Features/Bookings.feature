Feature: Hotel Room Booking

  Scenario: List rooms when empty
    When I request all rooms
    Then the response is 200 OK
    And the room list is empty

  Scenario: Add a room
    When I add a room with number "101" type "standard" and price 100
    Then the response is 201 Created
    And the room number is "101"
    And the room type is "standard"

  Scenario: List available rooms
    Given a room exists with number "201" type "deluxe" and price 150
    When I request available rooms
    Then the response is 200 OK
    And the room list has 1 room

  Scenario: Book a room
    Given a room exists with number "301" type "suite" and price 300
    When I book the room for guest "Alice" from "2025-06-01" to "2025-06-03"
    Then the response is 201 Created
    And the booking guest is "Alice"
    And the booking status is "confirmed"

  Scenario: Get booking details
    Given a room exists with number "401" type "standard" and price 120
    And a booking exists for guest "Bob" from "2025-07-01" to "2025-07-02"
    When I get the booking details
    Then the response is 200 OK
    And the booking guest is "Bob"

  Scenario: List all bookings
    Given a room exists with number "501" type "deluxe" and price 200
    And a booking exists for guest "Carol" from "2025-08-01" to "2025-08-05"
    When I request all bookings
    Then the response is 200 OK
    And the booking list has 1 booking

  Scenario: Cancel a booking
    Given a room exists with number "601" type "standard" and price 100
    And a booking exists for guest "Dave" from "2025-09-01" to "2025-09-03"
    When I cancel the booking
    Then the response is 200 OK
    And the booking status is "cancelled"

  Scenario: Cancelled booking frees up the room
    Given a room exists with number "701" type "standard" and price 100
    And a booking exists for guest "Eve" from "2025-10-01" to "2025-10-02"
    When I cancel the booking
    And I check the room availability
    Then the room is available

  Scenario: Book multiple rooms
    Given a room exists with number "801" type "standard" and price 100
    And a booking exists for guest "Frank" from "2025-11-01" to "2025-11-02"
    And another room exists with number "802" type "deluxe" and price 200
    When I book the room for guest "Grace" from "2025-11-01" to "2025-11-03"
    Then the booking guest is "Grace"
    And the booking status is "confirmed"

  Scenario: Find bookings by guest name
    Given a room exists with number "901" type "standard" and price 100
    And a booking exists for guest "Heidi" from "2025-12-01" to "2025-12-02"
    When I search bookings by guest name "Heidi"
    Then the response is 200 OK
    And the booking list has 1 booking

  Scenario: Get a non-existent booking returns 404
    When I get a booking with id 9999
    Then the response is 404 Not Found

  Scenario: Add a standard room
    When I add a room with number "102" type "standard" and price 80
    Then the response is 201 Created
    And the room type is "standard"

  Scenario: Add a suite room
    When I add a room with number "501" type "suite" and price 500
    Then the response is 201 Created
    And the room type is "suite"

  Scenario: List bookings for a guest with multiple bookings
    Given a room exists with number "A1" type "standard" and price 100
    And a booking exists for guest "Ivan" from "2025-06-01" to "2025-06-02"
    And another room exists with number "A2" type "deluxe" and price 150
    And a booking exists for guest "Ivan" from "2025-06-03" to "2025-06-04"
    When I search bookings by guest name "Ivan"
    Then the booking list has 2 bookings

  Scenario: Room becomes unavailable after booking
    Given a room exists with number "B1" type "standard" and price 100
    When I book the room for guest "Judy" from "2025-07-01" to "2025-07-03"
    And I check the room availability
    Then the room is not available
