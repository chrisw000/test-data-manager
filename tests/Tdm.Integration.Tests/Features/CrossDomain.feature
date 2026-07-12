Feature: Cross-domain identity via the identity contract
  An external reference computes the owning domain's GUID locally — coordination
  without communication. Locally it can also seed a read-model projection row.

  Scenario: External customer reference agrees across databases without coordination
    When I run the TDM with:
      """
      @domain:Billing
      Feature: t
        Scenario: s
          Given an Account exists with name "Acme Account" and currency "GBP"
          And an external Customer reference "Acme Ltd" from Orders
          And an Invoice exists for Account "Acme Account" for Customer "Acme Ltd" with invoice number "INV-1" and amount "10.00" and status "Issued"
      """
    Then the run outcome is Succeeded
    And invoice "INV-1" carries the external customer id for "Acme Ltd" from domain "Orders"
    And a customer summary projection "Acme Ltd" exists with that id
    And the manifest records a reference resolved from "identityContract"
    And the manifest invoice id is a database-generated integer
