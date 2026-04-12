Feature: More Speakers

  # ── Speakers ─────────────────────────────────────────────────────────────

  Scenario: Register a speaker
    When I register a speaker with email "alice@test.com" first name "Alice" and last name "Speaker"
    Then the speaker first name should be "Alice"
    And the speaker email should be "alice@test.com"

  Scenario: Cannot register duplicate email
    Given a speaker with email "dup@test.com" is registered
    When I try to register a speaker with email "dup@test.com"
    Then the response status should be 409

  Scenario: Get all speakers
    Given a speaker named "Bob Test" with email "bob@test.com" exists in the database
    When I get all speakers
    Then there should be at least 1 speaker

  Scenario: Get by id returns 404 for missing speaker
    When I get a speaker by a random id
    Then the response status should be 404

  Scenario: Update speaker profile
    Given a speaker named "Original" with email "orig@test.com" exists in the database
    When I update the speaker first name to "Updated" with 3 max mentees and expertise "C#"
    Then the updated speaker first name should be "Updated"
    And the speaker should be available for mentoring
    And the speaker max mentees should be 3
    And the speaker expertise should contain "C#"

  # ── Mentorships ──────────────────────────────────────────────────────────

  Scenario: Request mentorship
    Given a mentor and mentee exist
    When I request mentorship from the mentor
    Then the mentorship status should be "Pending"
    And the mentorship mentor id should match

  Scenario: Cannot mentor yourself
    Given a mentor and mentee exist
    When I try to request mentorship where mentor and mentee are the same
    Then the response status should be 400

  Scenario: Accept mentorship
    Given a pending mentorship exists
    When I accept the mentorship with message "Happy to help!"
    Then the mentorship status should be "Active"
    And the mentorship response should be "Happy to help!"

  Scenario: Cannot accept non-pending mentorship
    Given an active mentorship exists
    When I try to accept the mentorship
    Then the response status should be 400

  Scenario: Complete active mentorship
    Given an active mentorship exists
    When I complete the mentorship
    Then the mentorship status should be "Completed"
    And the mentorship completed at should be set
