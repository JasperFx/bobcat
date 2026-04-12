Feature: Ecommerce Modular Monolith

  # Catalog
  Scenario: Create catalog product
    When I create a catalog product named "Widget" with price 9.99
    Then the response status is 201
    And the catalog product id is returned

  Scenario: Get catalog products
    Given I create a catalog product named "Gadget" with price 19.99
    When I get all catalog products
    Then at least 1 catalog product is returned

  Scenario: Get catalog product by id
    Given I create a catalog product named "Gizmo" with price 5.99
    When I get the catalog product by id
    Then the response status is 200

  Scenario: Get catalog product by id 404
    When I get catalog product by id "00000000-0000-0000-0000-000000000000"
    Then the response status is 404

  Scenario: Update catalog product
    Given I create a catalog product named "OldWidget" with price 1.00
    When I update the catalog product name to "NewWidget"
    Then the response status is 200

  Scenario: Delete catalog product
    Given I create a catalog product named "DeleteMe" with price 1.00
    When I delete the catalog product
    Then the response status is 204

  # Basket
  Scenario: Store basket
    Given I create a catalog product named "BasketItem" with price 10.00
    When I store a basket for customer "customer1" with the product
    Then the response status is 201

  Scenario: Get basket
    Given I create a catalog product named "GetBasketItem" with price 10.00
    And I store a basket for customer "getBasketCustomer" with the product
    When I get the basket for customer "getBasketCustomer"
    Then the response status is 200

  Scenario: Delete basket
    Given I create a catalog product named "DelBasketItem" with price 10.00
    And I store a basket for customer "delBasketCustomer" with the product
    When I delete the basket for customer "delBasketCustomer"
    Then the response status is 204

  Scenario: Checkout basket
    Given I create a catalog product named "CheckoutItem" with price 25.00
    And I store a basket for customer "checkoutCustomer" with the product
    When I checkout the basket for customer "checkoutCustomer"
    Then the response status is 201

  # Ordering
  Scenario: Create an order
    Given I create a catalog product named "OrderItem" with price 15.00
    And I store a basket for customer "orderCustomer" with the product
    And I checkout the basket for customer "orderCustomer"
    When I get all orders
    Then at least 1 order is returned

  Scenario: Get all orders
    When I get all orders
    Then the response status is 200

  Scenario: Get order by id
    Given I create a catalog product named "OrderByIdItem" with price 15.00
    And I store a basket for customer "orderByIdCustomer" with the product
    And I checkout the basket for customer "orderByIdCustomer"
    When I get the first order
    Then the response status is 200

  Scenario: Delete an order
    Given I create a catalog product named "DeleteOrderItem" with price 15.00
    And I store a basket for customer "deleteOrderCustomer" with the product
    And I checkout the basket for customer "deleteOrderCustomer"
    When I delete the first order
    Then the response status is 204

  # Discount
  Scenario: Create a discount
    When I create a discount for product "DiscountedProduct" with percentage 10
    Then the response status is 201

  Scenario: Get discount by product name
    Given I create a discount for product "SearchedProduct" with percentage 15
    When I get the discount for product "SearchedProduct"
    Then the response status is 200

  Scenario: Update a discount
    Given I create a discount for product "UpdatableProduct" with percentage 5
    When I update the discount to percentage 20
    Then the response status is 200

  Scenario: Delete a discount
    Given I create a discount for product "DeletableDiscount" with percentage 10
    When I delete the discount
    Then the response status is 204
