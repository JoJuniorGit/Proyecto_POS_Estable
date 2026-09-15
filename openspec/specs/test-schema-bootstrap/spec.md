# test-schema-bootstrap Specification

## Purpose

Guarantee that the shared test database (`pos_test`) always carries BOTH the Sales and Inventory
schemas, regardless of which test class runs first, while preserving EF model parity.

## Requirements

### Requirement: Both Schemas Materialized on a Fresh Database

On a fresh database the bootstrap MUST materialize the Sales and Inventory models together, using the
marker tables `Users` (Sales) and `Products` (Inventory). The result MUST NOT depend on scheduling order.

#### Scenario: Inventory context bootstraps first

- GIVEN an empty `pos_test` with no tables
- WHEN the Inventory test class triggers the bootstrap first
- THEN both `Products` and `Users` exist
- AND Sales-dependent tests run without `42P01`

#### Scenario: Sales context bootstraps first

- GIVEN an empty `pos_test` with no tables
- WHEN the Sales test class triggers the bootstrap first
- THEN both `Users` and `Products` exist

### Requirement: Model Parity Preserved

The bootstrap MUST create tables from the EF model, not from migrations, so `Users.AccessFailedCount`
and model `HasData` rows exist. The shared test database MUST NOT use `Migrate` while the migration
gap exists.

#### Scenario: Model columns exist after bootstrap

- GIVEN the bootstrap has created the schemas
- WHEN the `Users` table columns are inspected
- THEN `AccessFailedCount` exists

#### Scenario: Model seed rows exist after bootstrap

- GIVEN the bootstrap has created the schemas
- WHEN the seeded default user is queried
- THEN the `HasData` row is present

### Requirement: Half-Initialized Database Repair

A legacy `pos_test` holding only one schema MUST be repaired by creating the missing one without
touching the existing tables.

#### Scenario: Inventory-only database gains Sales tables

- GIVEN `pos_test` with Inventory tables but no Sales tables
- WHEN the bootstrap runs
- THEN the missing Sales tables are created
- AND the existing Inventory tables are unchanged

### Requirement: Idempotent Bootstrap

The bootstrap MUST run at most once per connection string per process, serialized, and MUST no-op on
later invocations.

#### Scenario: Second invocation performs no DDL

- GIVEN the bootstrap already ran for a connection string in this process
- WHEN it is invoked again
- THEN it performs no schema creation

#### Scenario: Concurrent first callers are serialized

- GIVEN two test classes reach the bootstrap at the same time on the same connection string
- WHEN both call it
- THEN exactly one creation pass runs
- AND no `23505` on `pg_class_relname_nsp_index` occurs

### Requirement: Failure When a Marker Table Is Missing

After bootstrap, if either marker table is missing the run MUST fail with an actionable error naming it.

#### Scenario: Missing marker aborts the run

- GIVEN the bootstrap completed
- WHEN `Users` or `Products` is still absent
- THEN the run fails with an error naming the missing table

### Requirement: Non-PostgreSQL Providers Unaffected

InMemory and Sqlite test paths MUST NOT invoke the shared-schema bootstrap.

#### Scenario: InMemory context is untouched

- GIVEN an InMemory or Sqlite context created through the factory
- WHEN tests run
- THEN no shared-schema bootstrap executes
- AND the provider behavior is unchanged
