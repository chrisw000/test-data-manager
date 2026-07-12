@domain:Billing @seed:7
Feature: Billing cross-domain seeding
  External Customer references agree with the Orders domain via the TDM identity contract —
  no cross-database transaction, no runtime coordination (handoff §8.5).

  Scenario: Invoice for an externally-owned customer
    Given an Account exists with name "Acme Billing Account" and currency "GBP"
    And an external Customer reference "Acme Ltd" from Orders
    And an Invoice exists for Account "Acme Billing Account" for Customer "Acme Ltd" with invoice number "INV-2026-001" and amount "1250.00" and status "Issued"
    Then an Invoice "INV-2026-001" should exist with amount "1250.00"
    And a CustomerSummary "Acme Ltd" should exist

  Scenario: Delete all draft invoices
    Given an Account exists with name "Cleanup Account"
    And the following Invoices exist for Account "Cleanup Account":
      | InvoiceNumber | Amount | Status | IssuedDate |
      | INV-D-01      | 10.00  | Draft  | today-3d   |
      | INV-D-02      | 20.00  | Draft  | today-2d   |
      | INV-D-03      | 30.00  | Issued | today-1d   |
    When all Invoices with status "Draft" are deleted
    Then 0 Invoices should exist with status "Draft"
    And an Invoice "INV-D-03" should exist with status "Issued"
