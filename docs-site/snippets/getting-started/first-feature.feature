@seed:7
Feature: My first seed
  Scenario: A customer places their first order
    Given a Customer exists with name "Bluebird Books" and tier "Silver"
    And an Order exists for Customer "Bluebird Books" with order number "ORD-GS-1" and status "Pending"
    Then an Order "ORD-GS-1" should exist with status "Pending"
