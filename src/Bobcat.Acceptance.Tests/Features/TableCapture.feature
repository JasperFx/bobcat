Feature: Table Capture

  Scenario: A table step binds the capture in its text
    Given these steps ran in "Orders/talk to the broker"
      | kind | text                |
      | When | the broker is asked |
      | Then | it answers          |
    Then the log is "Orders/talk to the broker:When:the broker is asked|Orders/talk to the broker:Then:it answers"

  Scenario: Captures bind by role, not by position in the signature
    Given these 2 rows belong to "Acme"
      | label  |
      | first  |
      | second |
    Then the log is "Acme/2:first:ctx|Acme/2:second:ctx"
