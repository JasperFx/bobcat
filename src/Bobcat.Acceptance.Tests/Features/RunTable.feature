Feature: Run Table

  Scenario: A table run through the fixture's own method
    Given the members are
      | Member Name | tier       |
      | Ada         | Pro        |
      | Grace       | Enterprise |
    Then every member was logged

  Scenario: An optional column may be left out
    Given the members are
      | Member Name |
      | Ada         |
    Then every member was logged

  Scenario: A row whose cell will not convert fails that row alone
    Given the members are
      | Member Name | tier   |
      | Ada         | Pro    |
      | Grace       | Wizard |
      | Linus       | Free   |
    Then every member was logged

  Scenario: An async row method is awaited
    Given the members are added slowly
      | member |
      | Ada    |
      | Grace  |
    Then every member was logged

  Scenario: A decision table run by the fixture
    Then doubling gives
      | input | doubled |
      | 2     | 4       |
      | 3     | 7       |

  Scenario: A table of objects, with a relative date and a defaulted column
    Given the subscriptions are
      | Customer | Tier | RenewsOn |
      | Ada      | Pro  | TODAY+30 |
      | Grace    | Free | TODAY    |
