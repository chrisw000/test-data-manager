Feature: Core seeding verbs
  The TDM parses its own Gherkin grammar at runtime and persists generated,
  overridable entities into the sample Orders domain on SQLite.

  Scenario: Create with overrides persists via the domain repository
    When I run the TDM with:
      """
      Feature: t
        Scenario: s
          Given a Customer exists with name "Acme Ltd" and tier "Gold" and credit limit "25000"
      """
    Then the run outcome is Succeeded
    And the exit code is 0
    And the Orders database has 1 customer rows
    And customer "Acme Ltd" has tier "Gold"
    And the manifest entity "Customer" was persisted via "ICustomerWriteRepository"

  Scenario: DataTable bulk create
    When I run the TDM with:
      """
      Feature: t
        Scenario: s
          Given the following Products exist:
            | Sku  | Name    | Category |
            | A-1  | Widget  | Cat      |
            | B-2  | Gadget  | Cat      |
      """
    Then the run outcome is Succeeded
    And the Orders database has 2 product rows

  Scenario: Count bulk create records benchmark stats
    When I run the TDM with:
      """
      Feature: t
        Scenario: s
          Given 50 Products exist with category "Bulk"
      """
    Then the run outcome is Succeeded
    And the Orders database has 50 product rows
    And the manifest benchmark includes "create"

  Scenario: Update, load and delete verbs
    When I run the TDM with:
      """
      Feature: t
        Scenario: s
          Given a Customer exists with name "Acme Ltd" and tier "Gold"
          And a Product exists with sku "TMP-1" and category "Scratch"
          When the Customer "Acme Ltd" is updated with tier "Platinum"
          And all Products with category "Scratch" are deleted
          Then a Customer "Acme Ltd" should exist with tier "Platinum"
          And 0 Products should exist
      """
    Then the run outcome is Succeeded
    And customer "Acme Ltd" has tier "Platinum"
    And the Orders database has 0 product rows

  Scenario: Failed load assertion surfaces as a warning under BestEffort
    When I run the TDM with:
      """
      Feature: t
        Scenario: s
          Then a Customer "Ghost" should exist
      """
    Then the run outcome is CompletedWithWarnings
    And the exit code is 1
    And a manifest warning contains "Ghost"
