Feature: Table Grammar

  Scenario: Batched data setup
    Given the following customers exist
      | name  | orders |
      | Acme  | 3      |
      | Globex | 1     |

  Scenario: Decision table
    Then dividing gives
      | dividend | divisor | quotient |
      | 10       | 2       | 5        |
      | 9        | 3       | 4        |

  Scenario: A throwing Before still runs After
    Given the failing setup runs
      | label |
      | one   |
      | two   |
