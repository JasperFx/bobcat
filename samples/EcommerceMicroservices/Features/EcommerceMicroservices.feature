Feature: Ecommerce Microservices Products

  Scenario: Create a product
    When I create a product named "Widget" in category "Tools" with price 9.99
    Then the response status is 201
    And the product id is returned

  Scenario: Create product with invalid data returns 400
    When I create a product with empty name
    Then the response status is 400

  Scenario: Get product list
    Given I create a product named "Gadget" in category "Electronics" with price 29.99
    When I get all products
    Then at least 1 product is returned

  Scenario: Get product by id
    Given I create a product named "Gizmo" in category "Electronics" with price 14.99
    When I get the product by id
    Then the response status is 200
    And the product name is "Gizmo"

  Scenario: Get product by id returns 404 for missing
    When I get product by id "00000000-0000-0000-0000-000000000000"
    Then the response status is 404

  Scenario: Get products by category
    Given I create a product named "Sprocket" in category "Parts" with price 4.99
    When I get products in category "Parts"
    Then at least 1 product is returned

  Scenario: Update a product
    Given I create a product named "OldName" in category "Misc" with price 1.00
    When I update the product name to "NewName" with price 2.00
    Then the response status is 200

  Scenario: Update product with invalid data returns 400
    Given I create a product named "ValidProduct" in category "Misc" with price 1.00
    When I update the product with empty name
    Then the response status is 400

  Scenario: Delete a product
    Given I create a product named "ToDelete" in category "Misc" with price 1.00
    When I delete the product
    Then the response status is 204

  Scenario: Delete returns 404 for missing product
    When I delete product by id "00000000-0000-0000-0000-000000000000"
    Then the response status is 404

  Scenario: Delete nonexistent product returns 404
    Given I create a product named "Ephemeral" in category "Misc" with price 1.00
    And I delete the product
    When I delete the product
    Then the response status is 404
