Feature: Plugin-loaded domains
  The primary decoupled mode: domain assemblies load from a folder into an
  isolated AssemblyLoadContext, with EF version validation, and seed successfully.

  Scenario: Orders domain loads as an isolated plugin and seeds
    When I run the TDM with the Orders domain loaded as a plugin:
      """
      Feature: t
        Scenario: s
          Given a Customer exists with name "Plugin Co" and tier "Gold"
      """
    Then the run outcome is Succeeded
    And the Orders database has 1 customer rows
    And the manifest entity "Customer" was persisted via "ICustomerWriteRepository"
