Feature: Sets

  Storyteller's Sets samples, recreated. Six of these scenarios fail on purpose — a wrong cell, an
  extra row, a missing row, an expected value that will not parse, a broken query — because the
  samples exist to show what each outcome looks like in the grid.

  # Samples/Specs/Sets/Object_Sets.md — "This declaration is all out of order with the actual
  # data, but that's okay in this case"
  Scenario: An unordered set in a different order from the data
    Given the invoice details are
      | amount | date  | name       |
      | 100    | TODAY | The Shirts |
      | 200    | TODAY-1 | The Pants |
      | 10     | TODAY-2 | Socks     |
    Then the unordered details should be
      | Amount | Date    | Name       |
      | 10     | TODAY-2 | Socks      |
      | 200    | TODAY-1 | The Pants  |
      | 100    | TODAY   | The Shirts |

  # the same, "This time, some of the cell values are wrong"
  Scenario: Every cell is part of the evaluation
    Given the invoice details are
      | amount | date  | name       |
      | 100    | TODAY | The Shirts |
      | 200    | TODAY-1 | The Pants |
      | 10     | TODAY-2 | Socks     |
    Then the unordered details should be
      | Amount | Date    | Name       |
      | 11     | TODAY-2 | Socks      |
      | 200    | TODAY-5 | The Pants  |
      | 100    | TODAY   | The Shirts |

  # Samples/Specs/Sets/Object_Sets.md — "Now, let's do the same results where order matters. The
  # table below will fail because the ordering is wrong"
  Scenario: An ordered set whose rows are in the wrong order
    Given the invoice details are
      | amount | date  | name       |
      | 100    | TODAY | The Shirts |
      | 200    | TODAY-1 | The Pants |
      | 10     | TODAY-2 | Socks     |
    Then the ordered details should be
      | Amount | Date    | Name       |
      | 10     | TODAY-2 | Socks      |
      | 200    | TODAY-1 | The Pants  |
      | 100    | TODAY   | The Shirts |

  # StoryTeller.Samples/Specs/Sets/Ordered Set.md — "Completely successful ordering"
  Scenario: An ordered set in the order the specification writes it
    Given the invoice details are
      | amount | date    | name   |
      | 100.1  | TODAY   | Cord   |
      | 200.2  | TODAY+1 | Drill  |
      | 300.3  | TODAY+2 | Hammer |
    Then the ordered details should be
      | Amount | Date    | Name   |
      | 100.1  | TODAY   | Cord   |
      | 200.2  | TODAY+1 | Drill  |
      | 300.3  | TODAY+2 | Hammer |

  # the same file, "Should have one exra" — an insertion is one extra row, not a reordering
  Scenario: An ordered set with a row the specification does not describe
    Given the invoice details are
      | amount | date    | name   |
      | 100.1  | TODAY   | Cord   |
      | 200.2  | TODAY+1 | Drill  |
      | 300.3  | TODAY+2 | Hammer |
    Then the ordered details should be
      | Amount | Date    | Name  |
      | 100.1  | TODAY   | Cord  |
      | 300.3  | TODAY+2 | Hammer |

  # StoryTeller.Samples/Specs/Sets/Unordered Set.md — an expected cell that will not parse
  Scenario: An expected value that cannot be read
    Given the invoice details are
      | amount | date    | name   |
      | 100.1  | TODAY   | Cord   |
      | 200.2  | TODAY+1 | Drill  |
      | 300.3  | TODAY+2 | Hammer |
    Then the unordered details should be
      | Amount  | Date    | Name   |
      | invalid | TODAY+3 | Drill  |
      | 300.3   | TODAY+2 | Hammer |

  # Samples/Specs/Sets/Data_Tables.md — "Happy Path"
  Scenario: Every row in the database is accounted for
    Given the cities in the database are
      | city        | distance | zip   |
      | Austin      | 5        | 78750 |
      | Jasper      | 600      | 64755 |
      | Bentonville | 550      | 72712 |
    Then the rows in the database should be
      | City        | Distance | Zip   |
      | Austin      | 5        | 78750 |
      | Jasper      | 600      | 64755 |
      | Bentonville | 550      | 72712 |

  # "Extra Rows Detected from the Database"
  Scenario: The database has a row the specification does not
    Given the cities in the database are
      | city        | distance | zip   |
      | Austin      | 5        | 78750 |
      | Jasper      | 600      | 64755 |
      | Bentonville | 550      | 72712 |
    Then the rows in the database should be
      | City   | Distance | Zip   |
      | Austin | 5        | 78750 |
      | Jasper | 600      | 64755 |

  # "Missing Rows in the Database"
  Scenario: The specification expects a row the database does not have
    Given the cities in the database are
      | city        | distance | zip   |
      | Austin      | 5        | 78750 |
      | Jasper      | 600      | 64755 |
      | Bentonville | 550      | 72712 |
    Then the rows in the database should be
      | City        | Distance | Zip   |
      | Austin      | 5        | 78750 |
      | Jasper      | 600      | 64755 |
      | Bentonville | 550      | 72712 |
      | Joplin      | 575      | 64801 |

  # "Mismatch in Rows" — the key column itself disagrees, so it reads as one missing and one extra
  Scenario: A row whose key column disagrees
    Given the cities in the database are
      | city        | distance | zip   |
      | Austin      | 5        | 78750 |
      | Jasper      | 600      | 64755 |
      | Bentonville | 550      | 72712 |
    Then the rows in the database should be
      | City       | Distance | Zip   |
      | Round Rock | 5        | 78750 |
      | Jasper     | 600      | 64755 |
      | Bentonville | 550     | 72712 |

  # Samples/Specs/Sets/String_Lists.md — a set of plain strings, and the order is wrong
  Scenario: A set of names in the wrong order
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

  # the same file, "Chewie got lost, so this fails too" / "Leia wasn't with them at the time"
  Scenario: A set of names missing one and expecting one that is not there
    Given the names are
      | name   |
      | Luke   |
      | Han    |
      | Chewie |
    Then the names in order should be
      | Name  |
      | Luke  |
      | Han   |
      | Leia  |

  # StoryTeller.Samples/Specs/Sets/SetWithError.md
  Scenario: The query behind the set throws
    Given the query is broken
    Then the rows in the database should be
      | City   | Distance | Zip   |
      | Austin | 5        | 78750 |

  # Samples/Specs/Sets/Set_that_uses_a_non_primitive_type.md — "Green is going to be an extra here"
  Scenario: A set whose column is not a primitive
    Given the colours are
      | colour |
      | Blue   |
      | Red    |
      | Orange |
      | Green  |
    Then the colours should be
      | Colour |
      | Blue   |
      | Red    |
      | Orange |
