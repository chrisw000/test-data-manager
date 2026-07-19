@seed:7
Feature: Profile demo seed
  A spread of rows so `tdm profile` has real shapes to summarise (never row values).

  Scenario: Catalogue with varied categories
    Given all Products with category "Books" are deleted
    And all Products with category "Gadgets" are deleted
    And 200 Products exist with category "Books"
    And 100 Products exist with category "Gadgets"
    Then 300 Products should exist
