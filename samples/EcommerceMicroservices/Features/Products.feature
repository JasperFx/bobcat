Feature: Product Catalog

  Scenario: List products when empty
    When I request the product list
    Then the response is 200 OK
    And the product list is empty

  Scenario: Create a product
    When I create a product with name "Widget" price 10 stock 100 and category "Tools"
    Then the response is 201 Created
    And the product name is "Widget"
    And the product category is "Tools"

  Scenario: Get a product by id
    Given a product exists with name "Gadget" price 25 stock 50 and category "Electronics"
    When I get the product by id
    Then the response is 200 OK
    And the product name is "Gadget"

  Scenario: Update a product price
    Given a product exists with name "Thingamajig" price 5 stock 20 and category "Misc"
    When I update the product price to 15
    Then the response is 200 OK
    And the product price is 15

  Scenario: Delete a product
    Given a product exists with name "Doohickey" price 3 stock 10 and category "Misc"
    When I delete the product
    Then the response is 204 No Content

  Scenario: List products by category
    Given a product exists with name "Hammer" price 12 stock 30 and category "Tools"
    And a product exists with name "Wrench" price 8 stock 45 and category "Tools"
    And a product exists with name "Laptop" price 999 stock 5 and category "Electronics"
    When I filter products by category "Tools"
    Then the response is 200 OK
    And the product list has 2 items
    And all products in the list have category "Tools"

  Scenario: Get a non-existent product returns 404
    When I get a non-existent product with id 9999
    Then the response is 404 Not Found

  Scenario: Create product with stock count
    When I create a product with name "Bolt Pack" price 2 stock 500 and category "Hardware"
    Then the response is 201 Created
    And the product name is "Bolt Pack"
    And the product stock is 500
