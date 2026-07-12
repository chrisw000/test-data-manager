Feature: Determinism
  Identical feature file + seed + faker versions produce identical data,
  in completely separate environments.

  Scenario: Same seed produces identical data in fresh environments
    When I run the same TDM feature in two fresh environments with seed 42 and seed 42:
      """
      Feature: t
        Scenario: s
          Given a Customer exists
      """
    Then the generated customer emails match

  Scenario: Different seeds produce different data
    When I run the same TDM feature in two fresh environments with seed 42 and seed 99:
      """
      Feature: t
        Scenario: s
          Given a Customer exists
      """
    Then the generated customer emails differ
