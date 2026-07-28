Feature: Ef Recipe

  Scenario: Columns construct entities with no Row body
    Given the following customers exist
      | Name   | Region | Orders |
      | Acme   | West   | 3      |
      | Globex | East   | 1      |

  Scenario: A Row override customizes construction
    Given the following premium customers exist
      | name    | orders |
      | Initech | 2      |
