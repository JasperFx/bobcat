Feature: Ecommerce Modular Monolith

  # ── Catalog ──────────────────────────────────────────────────────────────

  Scenario: Create a product
    When I create a product named "Widget" in category "Electronics" with price 29.99
    Then the product id should be valid
    And the product name should be "Widget"
    And the product price should be 29.99

  Scenario: Update a product
    Given a product named "Original" in category "Cat" with price 10 exists
    When I update the product to name "Updated" and price 20
    Then the product name should be "Updated"
    And the product price should be 20

  Scenario: Delete a product
    Given a product named "ToDelete" in category "Cat" with price 5 exists
    When I delete the product
    And I get the product by id
    Then the response status should be 404

  Scenario: Get all products
    Given a product named "ProdA" in category "CatA" with price 10 exists
    When I get all products
    Then there should be at least 1 product

  Scenario: Get product by id
    Given a product named "ByIdProd" in category "Cat" with price 15 exists
    When I get the product by id
    Then the product name should be "ByIdProd"

  Scenario: Get products by category
    Given a product named "CatProd" in category "Electronics" with price 30 exists
    When I get products by category "Electronics"
    Then there should be at least 1 product

  # ── Basket ───────────────────────────────────────────────────────────────

  Scenario: Store and get basket
    When I store a basket for user "testuser" with product "Widget" quantity 2 and price 10
    And I get the basket for user "testuser"
    Then the basket user id should be "testuser"
    And the basket should have 1 item

  Scenario: Delete basket
    Given a basket for user "deleteuser" with product "X" quantity 1 and price 5 is stored
    When I delete the basket for user "deleteuser"
    And I get the basket for user "deleteuser"
    Then the response status should be 404

  Scenario: Checkout basket
    Given a basket for user "checkoutuser" with product "Laptop" quantity 1 and price 999 is stored
    When I checkout the basket for user "checkoutuser"
    Then the response status should be 200
    And the basket for user "checkoutuser" should be gone

  # ── Ordering ─────────────────────────────────────────────────────────────

  Scenario: Create an order
    When I create an order named "TestOrder-1"
    Then the order name should be "TestOrder-1"
    And the order status should be "Pending"

  Scenario: Get orders
    Given an order named "Order-A" exists
    When I get all orders
    Then there should be at least 1 order

  Scenario: Get order by id
    Given an order named "FindOrder" exists
    When I get the order by id
    Then the order name should be "FindOrder"

  Scenario: Delete an order
    Given an order named "DeleteOrder" exists
    When I delete the order
    And I get the order by id
    Then the response status should be 404

  # ── Discount ─────────────────────────────────────────────────────────────

  Scenario: Create a coupon
    When I create a coupon for product "TestProduct" with amount 25
    Then the coupon product name should be "TestProduct"
    And the coupon amount should be 25

  Scenario: Get coupon by product name
    Given a coupon for product "UniqueProd" with amount 50 exists
    When I get the coupon for product "UniqueProd"
    Then the coupon product name should be "UniqueProd"

  Scenario: Update a coupon
    Given a coupon for product "UpdProd" with amount 10 exists
    When I update the coupon description to "Updated" and amount to 20
    Then the coupon description should be "Updated"
    And the coupon amount should be 20

  Scenario: Delete a coupon
    Given a coupon for product "DelProd" with amount 5 exists
    When I delete the coupon
    And I get the coupon for product "DelProd"
    Then the response status should be 404
