@seed:42
Feature: Orders regression seed
  Demonstrates every TDM grammar verb against the Orders domain (modern convention profile).

  Background:
    Given a Customer exists with name "Acme Ltd" and tier "Gold" and credit limit "25000"

  Scenario: Customer places an order
    Given an Order exists for Customer "Acme Ltd" with order number "ORD-1001" and status "Pending" and total "199.99"
    Then an Order "ORD-1001" should exist with status "Pending"
    And a Customer "Acme Ltd" should exist with tier "Gold"

  Scenario: Bulk catalogue from a table
    Given the following Products exist:
      | Sku          | Name            | Price  | Category |
      | WID-0001     | Standard Widget | 9.99   | Widgets  |
      | WID-0002     | Deluxe Widget   | 24.50  | Widgets  |
      | GAD-0001     | Pocket Gadget   | 149.00 | Gadgets  |
    Then 2 Products should exist with category "Widgets"

  Scenario: Bulk load generation
    Given all Products with category "LoadTest" are deleted
    And 500 Products exist with category "LoadTest"
    Then 500 Products should exist with category "LoadTest"

  Scenario: Update and delete
    Given a Product exists with sku "TMP-0001" and name "Temporary" and category "Scratch"
    When the Customer "Acme Ltd" is updated with tier "Platinum"
    And the Product "TMP-0001" is deleted
    Then a Customer "Acme Ltd" should exist with tier "Platinum"
    And 0 Products should exist with sku "TMP-0001"

  Scenario Outline: Tiered customers via examples
    Given a Customer exists with name "<name>" and tier "<tier>"
    Then a Customer "<name>" should exist with tier "<tier>"

    Examples:
      | name          | tier   |
      | Globex Corp   | Silver |
      | Initech Ltd   | Gold   |

  @skip
  Scenario: Not yet implemented
    Given a Customer exists with name "Skipped Co"
