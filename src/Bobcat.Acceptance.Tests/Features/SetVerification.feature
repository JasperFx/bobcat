Feature: Set Verification

  Scenario: An unordered set does not care what order the rows arrive in
    Given the details are
      | name   | amount |
      | Cord   | 100    |
      | Drill  | 200    |
    Then the details should be
      | Name   | Amount |
      | Drill  | 200    |
      | Cord   | 100    |

  Scenario: An ordered set passes when the order agrees
    Given the details are
      | name   | amount |
      | Cord   | 100    |
      | Drill  | 200    |
    Then the details in order should be
      | Name   | Amount |
      | Cord   | 100    |
      | Drill  | 200    |

  Scenario: An ordered set reports the row that turned up early
    Given the details are
      | name   | amount |
      | Cord   | 100    |
      | Drill  | 200    |
    Then the details in order should be
      | Name   | Amount |
      | Drill  | 200    |
      | Cord   | 100    |

  Scenario: A set of plain values is compared under the column the fixture names
    Given the names are
      | name   |
      | Luke   |
      | Han    |
      | Chewie |
    Then the names in order should be
      | Name   |
      | Luke   |
      | Chewie |
      | Han    |

  Scenario: A column carries a header of its own
    Given the roster is
      | Player Name | grade  |
      | Willy Mays  | Gold   |
      | Nolan Ryan  | Silver |
    Then the roster reads "Willy Mays:Gold|Nolan Ryan:Silver"

  Scenario: An optional column may be left out of the table
    Given the roster is
      | Player Name |
      | Willy Mays  |
    Then the roster reads "Willy Mays:Bronze"

  Scenario: A table with a header and no rows says the set is empty
    Given the details are
      | name   | amount |
      | Cord   | 100    |
    Then the details should be
      | Name   | Amount |

  Scenario: A property titled for the document is compared under its title
    Given the ledger is
      | name   | amount |
      | Cord   | 100    |
      | Drill  | 200    |
    Then the ledger should be
      | Line Item | The Amount |
      | Drill     | 200        |
      | Cord      | 100        |

  Scenario: A titled column is not also known by the property name
    Given the ledger is
      | name   | amount |
      | Cord   | 100    |
    Then the ledger should be
      | Name   | Amount |
      | Cord   | 100    |
