Feature: Tables

  Storyteller's Tables samples, recreated. Five of these scenarios fail on purpose — a wrong
  expected cell, a cell that will not convert, a division by zero, a Before that throws and an
  After that throws — because the samples exist to show what each outcome looks like.

  # Samples/Specs/Tables/Using_[ExposeAsTable].md
  Scenario: A table from a method's return value
    Then adding numbers together
      | x | y | sum |
      | 1 | 1 | 2   |
      | 3 | 4 | 7   |
      | 4 | 9 | 13  |

  # StoryTeller.Samples/Specs/Tables/Tables.md — one wrong answer. Its first row was
  # | a | b | c |, three cells that cannot be read as ints; in Bobcat that is BOBCAT030 at
  # build time rather than a yellow cell at run time. See the README.
  Scenario: A table with a wrong expectation
    Then adding numbers together
      | x | y | sum |
      | 2 | 2 | 4   |
      | 2 | 2 | 5   |

  # StoryTeller.Samples/Specs/Tables/Decision Table.md — two computed columns, one row wrong
  Scenario: A decision table with two outputs
    Then what's my name?
      | FirstName | LastName | FullName      | LastNameFirst  |
      | Jeremy    | Miller   | Jeremy Miller | Miller, Jeremy |
      | Tim       | Tebow    | Tim Tebow     | Tebow, Tim     |

  Scenario: A decision table where both outputs disagree
    Then what's my name?
      | FirstName | LastName | FullName      | LastNameFirst  |
      | Jeremy    | Miller   | Jeremy Miller | Miller, Jeremy |
      | Hank      | Hill     | Hank Miller   | Miller Hank    |

  # Samples/Specs/Tables/Using_a_Paragraph.md, and the 3/0 row from Tables.md
  Scenario: A table of divisions
    Then dividing numbers
      | x  | y | quotient |
      | 10 | 5 | 2        |
      | 5  | 2 | 2.5      |

  Scenario: A row that throws
    Then dividing numbers
      | x  | y | quotient |
      | 10 | 5 | 2        |
      | 3  | 0 | 0        |
      | 9  | 3 | 3        |

  # StoryTeller.Samples/Specs/Tables/Boolean Results in a Table.md
  Scenario: A table of boolean answers
    Then is the number positive?
      | number | isPositive |
      | 5      | true       |
      | -5     | false      |

  # Samples/Specs/Tables/Before_and_After_Actions.md
  Scenario: Before and after actions around the rows
    Given the users are
      | first  | last   |
      | LeBron | James  |
      | James  | Harden |
      | Chris  | Paul   |
    Then the batch was saved once as "James, LeBron; Harden, James; Paul, Chris"

  # Samples/Specs/Tables/Table_with_Options.md
  Scenario: A table whose columns have headers and defaults
    Given the roster is
      | player       | position |
      | Nolan Ryan   | Pitcher  |
      | Willy Mays   | Outfield |
      | Johnny Bench | Catcher  |
    Then the roster reads "Nolan Ryan (Pitcher), Willy Mays (Outfield), Johnny Bench (Catcher)"

  # StoryTeller.Samples/Specs/Tables/Tables with Errors.md
  Scenario: The batch cannot be opened
    Given the batch with a broken open runs
      | x  |
      | 11 |
    Then the batch was closed anyway

  Scenario: The batch cannot be flushed
    Given the batch with a broken close runs
      | x  |
      | 22 |
    Then every row was seen
