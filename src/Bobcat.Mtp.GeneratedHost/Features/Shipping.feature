Feature: Shipping

  Scenario: A shipment is labelled
    Given a parcel weighing 3 kg
    Then the label reads "3 kg"
