Feature: Objects

  Storyteller's VerifyObject samples, recreated. Two of these four scenarios fail on purpose — one
  column wrong, and every column wrong — because the samples exist to show what a property check
  looks like when it disagrees.

  # Samples/Specs/Create Objects/Using_VerifyObject.md — three of the Address's six fields are
  # named and the other three are not meant by the document at all
  Scenario: Only the properties the document names are compared
    Given the address is
      | Address1     | Address2 | City   | StateOrProvince | Country | PostalCode |
      | 3 1st Street | EMPTY    | Dallas | TX              | US      | 75201      |
    Then the address should be
      | Address1     | Address2 | City   |
      | 3 1st Street | EMPTY    | Dallas |

  # StoryTeller.Samples/Specs/General/Check properties.md — "all the properties are correct"
  Scenario: Every named property agrees
    Given the address is
      | Address1      | City   | StateOrProvince |
      | 2 Second Lane | Austin | TX              |
    Then the address should be
      | Address1      | City   | StateOrProvince |
      | 2 Second Lane | Austin | TX              |

  # the same document, with one column wrong — the grid shows which, where the flattened
  # sentence this replaced made a reader parse it out
  Scenario: One property disagrees
    Given the address is
      | Address1      | City   | StateOrProvince |
      | 2 Second Lane | Austin | TX              |
    Then the address should be
      | Address1      | City   | StateOrProvince |
      | 2 Second Lane | Dallas | TX              |

  # "Check properties.md" runs the same shape with every column wrong
  Scenario: Every property disagrees
    Given the address is
      | Address1      | City   | StateOrProvince |
      | 2 Second Lane | Austin | TX              |
    Then the address should be
      | Address1    | City    | StateOrProvince |
      | 9 Ninth Way | Houston | OK              |
