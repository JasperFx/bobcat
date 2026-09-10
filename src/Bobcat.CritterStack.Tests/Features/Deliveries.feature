@domain:Logistics
Feature: Deliveries
  Triggered by the courier

  # The saga lane (issue #281). A saga starts the way the application starts it — here a
  # Wolverine message — so there is no arrange step. Storage can say a saga is active and what it
  # holds, or that none exists; it cannot tell "completed" from "never started", so the third
  # scenario shows the saga gone after the message that ends it, rather than claiming completion.

  Scenario: Booking a delivery starts its saga
    When DeliveryBooked is received
      | DeliverySagaId                       | Courier |
      | 44444444-4444-4444-4444-444444444444 | acme    |
    Then the DeliverySaga with id "44444444-4444-4444-4444-444444444444" is active
      | Courier | Status |
      | acme    | Booked |

  Scenario: Dispatching a delivery advances its saga
    When DeliveryBooked is received
      | DeliverySagaId                       | Courier |
      | 55555555-5555-5555-5555-555555555555 | acme    |
    When DeliveryDispatched is received
      | DeliverySagaId                       |
      | 55555555-5555-5555-5555-555555555555 |
    Then the DeliverySaga with id "55555555-5555-5555-5555-555555555555" is active
      | Status     |
      | Dispatched |

  Scenario: Confirming a delivery ends its saga
    When DeliveryBooked is received
      | DeliverySagaId                       | Courier |
      | 66666666-6666-6666-6666-666666666666 | acme    |
    When DeliveryConfirmed is received
      | DeliverySagaId                       |
      | 66666666-6666-6666-6666-666666666666 |
    Then no DeliverySaga exists with id "66666666-6666-6666-6666-666666666666"
