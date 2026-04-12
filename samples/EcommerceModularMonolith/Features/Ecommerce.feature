Feature: Ecommerce Modular Monolith

  Scenario: Create a product
    When I create a product with name "Widget" price 10 stock 100 category "Widgets"
    Then the response is 201 Created
    And the product name is "Widget"

  Scenario: List products
    Given a product exists with name "Gadget" price 25 stock 50 category "Gadgets"
    When I list all products
    Then the response is 200 OK
    And the product list has 1 items

  Scenario: Create a customer
    When I create a customer with name "Alice Smith" email "alice@example.com" address "123 Main St"
    Then the response is 201 Created
    And the customer name is "Alice Smith"

  Scenario: List customers
    Given a customer exists with name "Bob Jones" email "bob@example.com" address "456 Elm St"
    When I list all customers
    Then the response is 200 OK
    And the customer list has 1 items

  Scenario: Place an order
    Given a customer exists with name "Carol White" email "carol@example.com" address "789 Oak Ave"
    And a product exists with name "Sprocket" price 15 stock 20 category "Parts"
    When I place an order for product 1 quantity 2
    Then the response is 201 Created
    And the order status is "pending"

  Scenario: Get order details
    Given a customer exists with name "Dave Brown" email "dave@example.com" address "101 Pine Rd"
    And a product exists with name "Cog" price 5 stock 30 category "Parts"
    When I place an order for product 1 quantity 3
    And I get the order by id
    Then the response is 200 OK
    And the order status is "pending"

  Scenario: Order contains correct items
    Given a customer exists with name "Eve Green" email "eve@example.com" address "202 Birch Ln"
    And a product exists with name "Bolt" price 2 stock 100 category "Hardware"
    When I place an order for product 1 quantity 5
    And I get the order by id
    Then the order has 1 items
    And the order item product is "Bolt"

  Scenario: Placing an order reduces stock
    Given a customer exists with name "Frank Black" email "frank@example.com" address "303 Cedar Dr"
    And a product exists with name "Nut" price 1 stock 50 category "Hardware"
    When I place an order for product 1 quantity 10
    And I get the product by id
    Then the product stock is 40

  Scenario: Confirm an order
    Given a customer exists with name "Grace Hall" email "grace@example.com" address "404 Walnut Ct"
    And a product exists with name "Screw" price 3 stock 40 category "Hardware"
    When I place an order for product 1 quantity 2
    And I confirm the order
    Then the response is 200 OK
    And the order status is "confirmed"

  Scenario: Ship a confirmed order
    Given a customer exists with name "Hank Lee" email "hank@example.com" address "505 Maple Way"
    And a product exists with name "Washer" price 1 stock 60 category "Hardware"
    When I place an order for product 1 quantity 4
    And I confirm the order
    And I ship the order
    Then the response is 200 OK
    And the order status is "shipped"

  Scenario: Cancel a pending order
    Given a customer exists with name "Iris Chen" email "iris@example.com" address "606 Aspen Blvd"
    And a product exists with name "Rivet" price 2 stock 80 category "Hardware"
    When I place an order for product 1 quantity 5
    And I cancel the order
    Then the response is 200 OK
    And the order status is "cancelled"

  Scenario: Get orders for a customer
    Given a customer exists with name "Jack Wu" email "jack@example.com" address "707 Spruce Ave"
    And a product exists with name "Pin" price 1 stock 200 category "Hardware"
    When I place an order for product 1 quantity 1
    And I list orders for the customer
    Then the customer order list has 1 items

  Scenario: Customer with multiple orders
    Given a customer exists with name "Karen Davis" email "karen@example.com" address "808 Fir St"
    And a product exists with name "Clip" price 3 stock 100 category "Office"
    When I place an order for product 1 quantity 1
    And I place an order for product 1 quantity 2
    And I list orders for the customer
    Then the customer order list has 2 items

  Scenario: Update product stock
    Given a product exists with name "Spring" price 4 stock 25 category "Parts"
    When I update product stock by 10
    Then the response is 200 OK
    And the product stock is 35

  Scenario: Get a customer by id
    Given a customer exists with name "Leo Martin" email "leo@example.com" address "909 Poplar Rd"
    When I get the customer by id
    Then the response is 200 OK
    And the customer name is "Leo Martin"

  Scenario: Get a product by id
    Given a product exists with name "Gear" price 12 stock 15 category "Mechanical"
    When I get the product by id
    Then the response is 200 OK
    And the product name is "Gear"

  Scenario: List all orders
    Given a customer exists with name "Mia Taylor" email "mia@example.com" address "111 Larch St"
    And a product exists with name "Pulley" price 8 stock 30 category "Mechanical"
    When I place an order for product 1 quantity 2
    And I list all orders
    Then the response is 200 OK
    And the order list has 1 items

  Scenario: Order total is calculated correctly
    Given a customer exists with name "Nick Evans" email "nick@example.com" address "222 Willow Ct"
    And a product exists with name "Lever" price 6 stock 20 category "Mechanical"
    When I place an order for product 1 quantity 3
    And I get the order by id
    Then the order total is 18

  Scenario: Get a non-existent order returns 404
    When I get a non-existent order
    Then the response is 404 Not Found
