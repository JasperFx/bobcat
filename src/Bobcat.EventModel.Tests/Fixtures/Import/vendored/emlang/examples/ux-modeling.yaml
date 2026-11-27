# **Backend Event Model**
#
# The registration screen is a black box, reduced to the `Visitor/RegistrationForm` trigger.
# It connects to the UI model through the `RegisterUser` command and its response.
slices:
  # The visitor submits an email to create an account.
  # The email is always present. The backend has no "missing email" path.
  Register user:
    steps:
      - t: Visitor/RegistrationForm
      - c: RegisterUser
        props:
          email: string
      - e: User/UserRegistered
        props:
          userId: string
          email: string
    tests:
      # Defense in depth: the UI already blocks a malformed email,
      # but the backend never trusts the client.
      Email must be valid:
        when:
          - c: RegisterUser
            props:
              email: "not-an-email"
        then:
          - x: InvalidEmail
      Email must be unique:
        given:
          - e: User/UserRegistered
            props:
              email: "jane@example.com"
        when:
          - c: RegisterUser
            props:
              email: "jane@example.com"
        then:
          - x: EmailAlreadyInUse
      Registration succeeds:
        when:
          - c: RegisterUser
            props:
              email: "jane@example.com"
        then:
          - e: User/UserRegistered

---
# **UI Event Model**
#
# The backend is an external system, in the `Backend/` swimlane.
#
# - **Outbound**: `RegistrationSubmitted` carries the command to send (`sends`).
# - **Inbound**: the backend response comes in through translation slices.
#
# The UI never sees `UserRegistered`, only the response to `RegisterUser`.
#
# `RegistrationScreen` is the presentation model.
# In each slice, its props show the screen state after that slice.
slices:
  # The visitor opens the registration form.
  # Submit stays disabled until an email is entered.
  Open registration:
    steps:
      - t: Visitor/Register button
      - c: OpenRegistration
      - e: UI/RegistrationOpened
      - v: RegistrationScreen
        props:
          state: open
          email: ""
          canSubmit: false
          errorMessage: null
    tests:
      Submit disabled while email empty:
        given:
          - e: UI/RegistrationOpened
        then:
          - v: RegistrationScreen
            props:
              state: open
              canSubmit: false

  # Each change to the email updates the screen.
  # Submit is enabled only when the email is well formed.
  Change email:
    steps:
      - t: Visitor/Email field
      - c: ChangeEmail
        props:
          email: string
      - e: UI/EmailChanged
        props:
          email: string
      - v: RegistrationScreen
        props:
          state: open
          email: string
          canSubmit: boolean
          errorMessage: string | null
    tests:
      Submit disabled while email malformed:
        given:
          - e: UI/RegistrationOpened
          - e: UI/EmailChanged
            props:
              email: "jane@"
        then:
          - v: RegistrationScreen
            props:
              state: open
              canSubmit: false
              errorMessage: "The email is not valid."
      Submit enabled when email well formed:
        given:
          - e: UI/RegistrationOpened
          - e: UI/EmailChanged
            props:
              email: "jane@example.com"
        then:
          - v: RegistrationScreen
            props:
              state: open
              canSubmit: true
              errorMessage: null

  # **UI-specific business rules** guard the submission.
  # They do not exist in the backend model.
  #
  # Once the rules pass, `RegistrationSubmitted` sends `RegisterUser` to the backend.
  Submit registration:
    steps:
      - t: Visitor/Submit button
      - c: SubmitRegistration
      - e: UI/RegistrationSubmitted
        props:
          sends: RegisterUser
          email: string
      - v: RegistrationScreen
        props:
          state: submitting
          email: string
          canSubmit: false
          errorMessage: null
    tests:
      Email is required:
        given:
          - e: UI/RegistrationOpened
        when:
          - c: SubmitRegistration
        then:
          - x: EmailRequired
      Email must be well formed:
        given:
          - e: UI/RegistrationOpened
          - e: UI/EmailChanged
            props:
              email: "jane@"
        when:
          - c: SubmitRegistration
        then:
          - x: EmailMalformed
      # A second click while waiting for the backend is rejected.
      No double submission:
        given:
          - e: UI/RegistrationSubmitted
        when:
          - c: SubmitRegistration
        then:
          - x: SubmissionInProgress
      Submits when email is filled:
        given:
          - e: UI/RegistrationOpened
          - e: UI/EmailChanged
            props:
              email: "jane@example.com"
        when:
          - c: SubmitRegistration
        then:
          - e: UI/RegistrationSubmitted
            props:
              sends: RegisterUser
              email: "jane@example.com"
      Submitting while waiting:
        given:
          - e: UI/RegistrationSubmitted
        then:
          - v: RegistrationScreen
            props:
              state: submitting
              canSubmit: false

  # Translation slice. The backend accepted `RegisterUser`.
  #
  # No message is shown. The visitor goes straight to the dashboard,
  # another screen with its own UI model.
  Receive accepted registration:
    steps:
      - t: Backend/RegisterUserResponse
        props:
          outcome: accepted
      - c: RecordRegistrationAccepted
      - e: UI/RegistrationSucceeded
      - v: RegistrationScreen
        props:
          state: redirecting
          email: string
          canSubmit: false
          errorMessage: null
          navigateTo: Dashboard
    tests:
      Redirects to dashboard:
        given:
          - e: UI/RegistrationSubmitted
          - e: UI/RegistrationSucceeded
        then:
          - v: RegistrationScreen
            props:
              state: redirecting
              navigateTo: Dashboard

  # Translation slice. The backend rejected `RegisterUser`.
  #
  # The reason becomes an error message and the visitor can try again.
  Receive rejected registration:
    steps:
      - t: Backend/RegisterUserResponse
        props:
          outcome: rejected
          reason: InvalidEmail | EmailAlreadyInUse
      - c: RecordRegistrationRejected
        props:
          reason: string
      - e: UI/RegistrationFailed
        props:
          reason: string
      - v: RegistrationScreen
        props:
          state: failed
          email: string
          canSubmit: true
          errorMessage: string
    tests:
      Shows error and allows retry:
        given:
          - e: UI/RegistrationSubmitted
          - e: UI/RegistrationFailed
            props:
              reason: EmailAlreadyInUse
        then:
          - v: RegistrationScreen
            props:
              state: failed
              canSubmit: true
              errorMessage: "This email is already in use."

  # The visitor closes the form without submitting.
  Close registration:
    steps:
      - t: Visitor/Close button
      - c: CloseRegistration
      - e: UI/RegistrationClosed
      - v: RegistrationScreen
        props:
          state: closed
          email: ""
          canSubmit: false
          errorMessage: null
    tests:
      # The form stays open until the backend responds.
      Cannot close while submitting:
        given:
          - e: UI/RegistrationSubmitted
        when:
          - c: CloseRegistration
        then:
          - x: SubmissionInProgress
