Feature: Reference resolution
  References resolve from the scenario context bag first (fully deterministic),
  then fall back to a database lookup by natural key.

  Scenario: Reference resolves from the scenario context bag
    When I run the TDM with:
      """
      Feature: t
        Scenario: s
          Given a Customer exists with name "Acme Ltd"
          And an Order exists for Customer "Acme Ltd" with order number "ORD-1" and status "Pending"
      """
    Then the run outcome is Succeeded
    And order "ORD-1" is linked to customer "Acme Ltd"
    And the manifest records a reference resolved from "contextBag"

  Scenario: Reference falls back to a database lookup in a later run
    When I run the TDM with:
      """
      Feature: t
        Scenario: base seed
          Given a Customer exists with name "Acme Ltd"
      """
    And I run the TDM again with:
      """
      Feature: t
        Scenario: later run
          Given an Order exists for Customer "Acme Ltd" with order number "ORD-2" and status "Pending"
      """
    Then the run outcome is Succeeded
    And order "ORD-2" is linked to customer "Acme Ltd"
    And the manifest records a reference resolved from "database"

  Scenario: Deterministic identity derives from the natural key
    When I run the TDM with:
      """
      Feature: t
        Scenario: s
          Given a Customer exists with name "Acme Ltd"
      """
    Then customer "Acme Ltd" has the identity-contract id for domain "Orders"

  Scenario: Unresolvable reference is a warning under BestEffort
    When I run the TDM with:
      """
      Feature: t
        Scenario: s
          Given an Order exists for Customer "Nobody" with order number "ORD-3"
      """
    Then the run outcome is CompletedWithWarnings
    And a manifest warning contains "Nobody"
