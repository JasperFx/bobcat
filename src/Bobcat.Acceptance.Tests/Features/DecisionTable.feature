Feature: Decision Table

  Scenario: Return-value columns all pass
    Then the line totals are calculated
      | quantity | price | LineTotal |
      | 2        | 10.00 | 20.00     |
      | 3        | 5.00  | 15.00     |

  Scenario: Return-value column has a discrepancy
    Then the line totals are calculated
      | quantity | price | LineTotal |
      | 2        | 10.00 | 20.00     |
      | 4        | 2.00  | 9.00      |

  Scenario: Out-param columns all pass
    Then the divmod results are
      | dividend | divisor | quotient | remainder |
      | 17       | 5       | 3        | 2         |
      | 10       | 3       | 3        | 1         |

  Scenario: Out-param column has a discrepancy
    Then the divmod results are
      | dividend | divisor | quotient | remainder |
      | 17       | 5       | 3        | 9         |
