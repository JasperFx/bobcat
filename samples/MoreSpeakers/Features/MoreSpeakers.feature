Feature: More Speakers

  # Speaker management
  Scenario: Register a speaker
    When I register a speaker with email "speaker@conf.com" and name "Alice Speaker"
    Then the response status is 201
    And the speaker id is returned

  Scenario: Register duplicate email returns 409
    Given I register a speaker with email "dup.speaker@conf.com" and name "Dup Speaker"
    When I register a speaker with email "dup.speaker@conf.com" and name "Dup Speaker Again"
    Then the response status is 409

  Scenario: Get all speakers
    Given I register a speaker with email "list.speaker@conf.com" and name "Listed Speaker"
    When I get all speakers
    Then at least 1 speaker is returned

  Scenario: Get speaker by id returns 404 for missing
    When I get speaker by id "00000000-0000-0000-0000-000000000000"
    Then the response status is 404

  Scenario: Update speaker profile
    Given I register a speaker with email "update.speaker@conf.com" and name "Update Speaker"
    When I update the speaker bio to "Expert in distributed systems"
    Then the response status is 200

  # Mentorship
  Scenario: Request mentorship
    Given I register a speaker with email "mentee@conf.com" and name "Mentee Speaker"
    And I register a mentor with email "mentor@conf.com" and name "Mentor Speaker"
    When I request mentorship from the mentor
    Then the response status is 201
    And the mentorship id is returned

  Scenario: Self mentorship returns 400
    Given I register a speaker with email "self.mentor@conf.com" and name "Self Mentor"
    When I request mentorship from myself
    Then the response status is 400

  Scenario: Accept mentorship
    Given I register a speaker with email "accept.mentee@conf.com" and name "Accept Mentee"
    And I register a mentor with email "accept.mentor@conf.com" and name "Accept Mentor"
    And I request mentorship from the mentor
    When the mentor accepts the mentorship
    Then the response status is 200

  Scenario: Accept non-pending mentorship returns 400
    Given I register a speaker with email "nonpending.mentee@conf.com" and name "NonPending Mentee"
    And I register a mentor with email "nonpending.mentor@conf.com" and name "NonPending Mentor"
    And I request mentorship from the mentor
    And the mentor accepts the mentorship
    When the mentor accepts the mentorship
    Then the response status is 400

  Scenario: Complete mentorship
    Given I register a speaker with email "complete.mentee@conf.com" and name "Complete Mentee"
    And I register a mentor with email "complete.mentor@conf.com" and name "Complete Mentor"
    And I request mentorship from the mentor
    And the mentor accepts the mentorship
    When the mentor completes the mentorship
    Then the response status is 200
