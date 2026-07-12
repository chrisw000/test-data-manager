Feature: Failure policies
  BestEffort warns and skips, FailObject rejects the object, FailRun aborts the run.

  Scenario: BestEffort skips a bad property but persists the object
    Given the failure policy is BestEffort
    When I run the TDM with:
      """
      Feature: t
        Scenario: s
          Given a Customer exists with name "Acme Ltd" and credit limit "not-a-number"
      """
    Then the run outcome is CompletedWithWarnings
    And the Orders database has 1 customer rows
    And a manifest warning contains "CreditLimit"

  Scenario: FailObject rejects the whole object
    Given the failure policy is FailObject
    When I run the TDM with:
      """
      Feature: t
        Scenario: s
          Given a Customer exists with name "Acme Ltd" and credit limit "not-a-number"
      """
    Then the run outcome is CompletedWithWarnings
    And the Orders database has 0 customer rows

  Scenario: FailRun aborts the run and skips remaining scenarios
    Given the failure policy is FailRun
    When I run the TDM with:
      """
      Feature: t
        Scenario: bad
          Given a Customer exists with name "Acme Ltd" and credit limit "not-a-number"
        Scenario: never runs
          Given a Customer exists with name "Beta Corp"
      """
    Then the run outcome is Failed
    And the exit code is 2
    And the manifest run executed 1 scenario
    And the Orders database has 0 customer rows

  Scenario: Unmatched steps are recorded, not fatal
    Given the failure policy is BestEffort
    When I run the TDM with:
      """
      Feature: t
        Scenario: s
          Given the moon is made of cheese
          And a Customer exists with name "Acme Ltd"
      """
    Then the run outcome is CompletedWithWarnings
    And the manifest has 1 unmatched step
    And the Orders database has 1 customer rows
