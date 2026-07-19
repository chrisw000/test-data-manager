@seed:99
Feature: Perf demo bulk seed
  A bounded (5k-row) volume seed the docs-verify job runs end to end — seed, measure,
  publish, compare — against an isolated database, so it stays green regardless of the
  shared sample workspace's state.

  Scenario: Bulk product catalogue
    Given all Products with category "PerfDemo" are deleted
    And 5000 Products exist with category "PerfDemo"
    Then 5000 Products should exist with category "PerfDemo"
