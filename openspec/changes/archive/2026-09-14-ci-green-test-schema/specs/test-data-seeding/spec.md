# test-data-seeding Specification

## Purpose

Make standard sales seed data idempotent and concurrency-safe on the shared test database, with no test
writer bypassing the seeder.

## Requirements

### Requirement: Per-Row Idempotent Seeding

`SeedStandardSalesDataAsync` MUST create only the missing rows: PaymentMethods with deterministic Ids
1-5 (`"Punto de Venta"` = Id 3) and the default Customer.

#### Scenario: Model-seeded payment methods are not duplicated

- GIVEN a fresh schema where model `HasData` inserted PaymentMethods Ids 1 and 2
- WHEN `SeedStandardSalesDataAsync` runs
- THEN only the missing methods are inserted, keeping Ids 3-5 including `"Punto de Venta"` = 3
- AND Ids 1 and 2 are not reinserted

#### Scenario: Default customer is created when absent

- GIVEN a schema without the default customer
- WHEN the seeder runs
- THEN Customer Id 1 exists with `IsDefault = true`

#### Scenario: Repeat seeding is a no-op

- GIVEN the seeder already ran once
- WHEN it runs again
- THEN no duplicate rows are created
- AND no unique-index violation occurs

### Requirement: Advisory-Lock Serialization on Npgsql

On Npgsql the seeder MUST run inside a transaction holding `pg_advisory_xact_lock`, so concurrent
seeders cannot duplicate rows or produce FK references to missing rows.

#### Scenario: Concurrent seeders create rows exactly once

- GIVEN two Npgsql connections seed the same database concurrently
- WHEN both run under `pg_advisory_xact_lock`
- THEN the rows are created exactly once

#### Scenario: No payment references a missing method

- GIVEN concurrent seeding and sale-payment inserts
- WHEN the seed transaction commits
- THEN no `SalePayment` references a `PaymentMethodId` that was never seeded
- AND no `23503` occurs

### Requirement: No Test Bypasses the Seeder

`SalesServiceUnitTests.GetSalesHistoryAsync_AgainstRealPostgreSql_ReturnsSeededInvoices` MUST call the
seeder and resolve the payment method by name, and `DailyClosureRetryIntegrationTests` MUST resolve
`PaymentMethodId` by name.

#### Scenario: Sales history test uses the seeder

- GIVEN `GetSalesHistoryAsync_AgainstRealPostgreSql_ReturnsSeededInvoices`
- WHEN it needs `"Punto de Venta"`
- THEN it calls the seeder and resolves the method by name
- AND it does not insert its own duplicate method row

#### Scenario: Daily closure resolves the method by name

- GIVEN `DailyClosureRetryIntegrationTests`
- WHEN it needs a `PaymentMethodId`
- THEN it resolves the method by name instead of hardcoding Id 3

### Requirement: Non-PostgreSQL Seeding Preserved

InMemory and Sqlite seeding MUST keep the existing behavior and MUST NOT use advisory locks.

#### Scenario: InMemory seeding unchanged

- GIVEN an InMemory or Sqlite context
- WHEN the seeder runs
- THEN customers and payment methods are seeded as before
- AND no advisory lock is taken
