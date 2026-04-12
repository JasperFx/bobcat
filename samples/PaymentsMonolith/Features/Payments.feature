Feature: Payments Monolith

  Scenario: Create a payment account
    When I create an account with name "Alice" email "alice@pay.com" balance 1000
    Then the response is 201 Created
    And the account name is "Alice"

  Scenario: Get account details
    Given an account exists with name "Bob" email "bob@pay.com" balance 500
    When I get the account by id
    Then the response is 200 OK
    And the account name is "Bob"
    And the account balance is 500

  Scenario: Make a payment between accounts
    Given an account exists with name "Carol" email "carol@pay.com" balance 2000
    And a second account exists with name "Dave" email "dave@pay.com" balance 100
    When I make a payment of 300 from sender to receiver with reference "INV-001"
    Then the response is 201 Created
    And the payment status is "completed"
    And the payment amount is 300

  Scenario: Get payment details
    Given an account exists with name "Eve" email "eve@pay.com" balance 1500
    And a second account exists with name "Frank" email "frank@pay.com" balance 200
    When I make a payment of 100 from sender to receiver with reference "INV-002"
    And I get the payment by id
    Then the response is 200 OK
    And the payment reference is "INV-002"

  Scenario: Payment reduces sender balance
    Given an account exists with name "Grace" email "grace@pay.com" balance 800
    And a second account exists with name "Hank" email "hank@pay.com" balance 50
    When I make a payment of 200 from sender to receiver with reference "INV-003"
    And I get the sender account
    Then the account balance is 600

  Scenario: Payment increases receiver balance
    Given an account exists with name "Iris" email "iris@pay.com" balance 700
    And a second account exists with name "Jack" email "jack@pay.com" balance 300
    When I make a payment of 150 from sender to receiver with reference "INV-004"
    And I get the receiver account
    Then the account balance is 450

  Scenario: View payment history for account
    Given an account exists with name "Karen" email "karen@pay.com" balance 1000
    And a second account exists with name "Leo" email "leo@pay.com" balance 500
    When I make a payment of 100 from sender to receiver with reference "INV-005"
    And I view payment history for sender
    Then the payment history has 1 entries

  Scenario: Refund a payment
    Given an account exists with name "Mia" email "mia@pay.com" balance 600
    And a second account exists with name "Nick" email "nick@pay.com" balance 400
    When I make a payment of 50 from sender to receiver with reference "INV-006"
    And I refund the payment
    Then the response is 200 OK
    And the payment status is "refunded"

  Scenario: Filter payments by status
    Given an account exists with name "Olivia" email "olivia@pay.com" balance 900
    And a second account exists with name "Paul" email "paul@pay.com" balance 100
    When I make a payment of 75 from sender to receiver with reference "INV-007"
    And I filter payments by status "completed"
    Then the filtered payment list has 1 entries

  Scenario: Cannot get non-existent payment
    When I get a non-existent payment
    Then the response is 404 Not Found
