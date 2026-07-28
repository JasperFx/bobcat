Feature: Marten Recipe

  @readback
  Scenario: Columns construct documents with no Row body
    Given the following customers exist
      | Name   | Region | Orders |
      | Acme   | West   | 3      |
      | Globex | East   | 1      |
    Then the stored customers should be
      | Name   | Region | Orders |
      | Acme   | West   | 3      |
      | Globex | East   | 1      |

  Scenario: A Row override customizes construction
    Given the following premium customers exist
      | name    | orders |
      | Initech | 2      |
    Then the stored customers should be
      | Name          | Region  | Orders |
      | Initech       | Premium | 20     |
      | Initech-audit | Audit   | 0      |
