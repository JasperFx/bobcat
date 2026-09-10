@domain:Logistics
Feature: Shipments
  Triggered by the dispatcher

  # A DOCUMENT-backed application (issue #270): no event store, no stream, no projection. The
  # document lane arranges and asserts state; the HTTP lane acts. Neither grammar knows about the
  # other — the fixture composes both, and this feature is written entirely in shipped steps.

  Scenario: Booking a shipment writes it and cascades the command
    When ShipmentRequest is posted to "/shipments"
      | Origin | Destination | Carrier | WeightKg |
      | Dallas | Austin      | acme    | 12.5     |
    Then the response is 202
    And ShipmentBooked is sent

  Scenario: An arranged shipment can be read back
    Given documents of type Shipment
      | Id                                   | Origin | Destination | Status  |
      | 11111111-1111-1111-1111-111111111111 | Dallas | Austin      | Booked  |
      | 22222222-2222-2222-2222-222222222222 | Reno   | Boise       | Booked  |
    Then the Shipment with id "11111111-1111-1111-1111-111111111111" has
      | Origin | Status |
      | Dallas | Booked |

  # The arrange names only the columns the scenario depends on; WeightKg is not part of it.
  Scenario: Cancelling a shipment removes it
    Given documents of type Shipment
      | Id                                   | Origin | Destination | Status |
      | 33333333-3333-3333-3333-333333333333 | Tulsa  | Wichita     | Booked |
    When CancelShipmentRequest is posted to "/shipments/cancel"
      | ShipmentId                           |
      | 33333333-3333-3333-3333-333333333333 |
    Then the response is 200
    And no Shipment exists with id "33333333-3333-3333-3333-333333333333"
