Feature: Ecommerce Microservices Catalog

  Scenario: Create product returns product
    When I create a product named "Test Phone" in category "Smart Phone" with price 599.99
    Then the product id should be valid
    And the product name should be "Test Phone"
    And the product category should contain "Smart Phone"
    And the product price should be 599.99

  Scenario: Create product rejects empty name
    When I try to create a product with empty name in category "Phone" with price 100
    Then the response status should be 400

  Scenario: Create product rejects empty category
    When I try to create a product named "Phone" with empty category and price 100
    Then the response status should be 400

  Scenario: Create product rejects zero price
    When I try to create a product named "Phone" in category "Phone" with price 0
    Then the response status should be 400

  Scenario: Get products returns list
    Given a product named "Product A" in category "Cat1" with price 100 exists
    And a product named "Product B" in category "Cat2" with price 200 exists
    When I get all products
    Then there should be at least 2 products

  Scenario: Get product by id returns product
    Given a product named "ById Product" in category "Cat" with price 150 exists
    When I get the product by id
    Then the product name should be "ById Product"

  Scenario: Get product by id returns 404 for missing
    When I get a product by a random id
    Then the response status should be 404

  Scenario: Get products by category
    Given a product named "Cat Phone" in category "Smart Phone" with price 500 exists
    And a product named "Cat Laptop" in category "Laptop" with price 1200 exists
    When I get products by category "Smart Phone"
    Then all products should be in category "Smart Phone"

  Scenario: Update product modifies fields
    Given a product named "Original" in category "Cat" with price 100 exists
    When I update the product name to "Updated" category to "New Cat" and price to 250
    Then the product name should be "Updated"
    And the product category should contain "New Cat"
    And the product price should be 250

  Scenario: Update product rejects empty name
    Given a product named "ToUpdate" in category "Cat" with price 100 exists
    When I try to update the product with empty name
    Then the response status should be 400

  Scenario: Update product rejects zero price
    Given a product named "ToUpdate2" in category "Cat" with price 100 exists
    When I try to update the product with price 0
    Then the response status should be 400

  Scenario: Update nonexistent product returns 404
    When I try to update a product with a random id
    Then the response status should be 404

  Scenario: Delete product removes it
    Given a product named "ToDelete" in category "Cat" with price 100 exists
    When I delete the product
    And I get the product by id
    Then the response status should be 404

  Scenario: Delete nonexistent product returns 404
    When I try to delete a product with a random id
    Then the response status should be 404
