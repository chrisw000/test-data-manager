@seed:21
Feature: Fulfilment complex-domain seeding
  Walks every edge case the "Testing complex domains" guide documents. CI builds and seeds
  this on the SQLite leg, so the guide can only describe features that actually run.

  Background:
    # Self-referencing hierarchy: Site → Aisle → Bin, each parent a prior Location.
    Given a Location exists with code "SITE-1" and name "Primary Site" and kind "Site"
    And a Location exists with code "AISLE-A" and name "Aisle A" and kind "Aisle" for Location "SITE-1"
    And a Location exists with code "BIN-A1" and name "Bin A1" and kind "Bin" for Location "AISLE-A"

  Scenario: A shipment for an externally-owned order
    # Three-domain identity chain: Orders owns the Order; Fulfilment references it by number.
    Given an external Order reference "ORD-1001" from Orders
    And a Shipment exists for Order "ORD-1001" for Location "BIN-A1" with shipment number "SHP-00001" and status "Dispatched"
    Then a Shipment "SHP-00001" should exist with status "Dispatched"

  Scenario: A bulk of shipments with realistic status, carrier and delivery windows
    # Server-assigned long keys, enum-heavy weighted status, correlated carrier/service
    # level from the dataset, and DateOnly/TimeOnly delivery windows — all generated.
    Given an external Order reference "ORD-1002" from Orders
    And 50 Shipments exist for Order "ORD-1002" for Location "BIN-A1"
    Then 50 Shipments should exist
