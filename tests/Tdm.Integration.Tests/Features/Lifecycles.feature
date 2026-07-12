Feature: Lifecycle modes
  Persistent leaves data behind, Transactional rolls the whole scenario back,
  TrackedTeardown commits then destroys created rows in reverse dependency order.

  Scenario: Transactional leaves no residue
    Given the lifecycle is Transactional
    When I run the TDM with:
      """
      Feature: t
        Scenario: s
          Given a Product exists with sku "TX-1"
          And a Product exists with sku "TX-2"
      """
    Then the run outcome is Succeeded
    And the Orders database has 0 product rows

  Scenario: TrackedTeardown destroys created rows at scenario end
    Given the lifecycle is TrackedTeardown
    When I run the TDM with:
      """
      Feature: t
        Scenario: s
          Given a Customer exists with name "Fleeting Co"
          And an Order exists for Customer "Fleeting Co" with order number "ORD-T1"
      """
    Then the run outcome is Succeeded
    And the manifest scenario teardown deleted 2 rows
    And the Orders database has 0 customer rows
    And the Orders database has 0 order rows

  Scenario: Persistent leaves data behind
    Given the lifecycle is Persistent
    When I run the TDM with:
      """
      Feature: t
        Scenario: s
          Given a Product exists with sku "P-1"
      """
    Then the Orders database has 1 product rows

  Scenario: Ephemeral tag overrides a persistent run
    Given the lifecycle is Persistent
    When I run the TDM with:
      """
      Feature: t
        @ephemeral
        Scenario: s
          Given a Product exists with sku "P-1"
      """
    Then the Orders database has 0 product rows
