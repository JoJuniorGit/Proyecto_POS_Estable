# cash-closure-arqueo-window Specification

## Purpose

Defines how the daily-closure arqueo resolves the time window used to rebuild expected
totals, so night-shift sales are not silently dropped and the reported totals match the
drawer session that physically holds the cash.

## Requirements

### Requirement: Session-Anchored Window Precedence

The window resolver MUST return the start as
`activeSession.OpenedAt ?? lastClosure.ClosureDate ?? startOfDayUtc`, where `startOfDayUtc`
is the start of the query's Venezuela business day, and MUST NOT clamp the start to that
calendar-day boundary.

#### Scenario: Open session anchors the window

- GIVEN a drawer session opened at 2026-09-14 20:00 local and a last closure at 2026-09-14 23:00
- WHEN expected totals are requested for the closure run at 2026-09-15 00:15
- THEN the window start MUST be the session `OpenedAt` (2026-09-14 20:00)
- AND sales and payments from 23:00 to 23:59:59 MUST be included in the arqueo

#### Scenario: No session falls back to last closure, then start of day

- GIVEN no open drawer session
- WHEN the last closure exists, or none exists at all
- THEN the start MUST be the last closure date, or `startOfDayUtc` when no closure exists

### Requirement: Half-Open Interval Boundaries

Expected totals MUST include rows with `timestamp >= StartUtc` and MUST exclude rows with
`timestamp >= EndExclusiveUtc`, where `EndExclusiveUtc` is `startOfDayUtc.AddDays(1)`.

#### Scenario: Row exactly at the window start

- GIVEN a sale timestamped exactly at `StartUtc`
- WHEN the arqueo is computed
- THEN the sale MUST be included

#### Scenario: Row exactly at the right bound

- GIVEN a sale timestamped exactly at `EndExclusiveUtc`
- WHEN the arqueo is computed
- THEN the sale MUST be excluded

### Requirement: Backdated Closure Fallback

When a resolved session-anchored window is invalid or empty (`StartUtc >= EndExclusiveUtc`)
— only for an Admin-backdated closure within 24h — the window start MUST fall back to
`lastClosure.ClosureDate ?? startOfDayUtc`, bounded below `EndExclusiveUtc`, guaranteeing a
valid non-empty window (`StartUtc < EndExclusiveUtc`).

#### Scenario: Backdated closure behind the current session

- GIVEN a backdated closure whose date precedes the active session `OpenedAt`
- WHEN the resolved session-anchored window would be empty
- THEN the window start MUST be `lastClosure.ClosureDate ?? startOfDayUtc`
- AND the window start MUST be strictly less than `EndExclusiveUtc`

### Requirement: Pure Resolver and Immutable Snapshots

The window math MUST live in a pure resolver returning `(StartUtc, EndExclusiveUtc)` with
no `DbContext` dependency, and persisted closure values MUST NOT be recomputed or rewritten.

#### Scenario: Resolver is unit-testable without a database

- GIVEN the resolver inputs (session open time, last closure date, query date)
- WHEN the resolver is invoked
- THEN it returns the window with no `DbContext`

#### Scenario: A persisted closure is never rewritten

- GIVEN a closure already persisted with its `ClosureDetail.ExpectedAmountBsS`
- WHEN a later arqueo or preview runs
- THEN the persisted values MUST NOT change

### Requirement: Preview Requires an Explicit Date

The preview endpoint `DailyClosureController.GetExpectedTotals`
(`GET /api/DailyClosure/expected-totals`) MUST reject a missing or default (`default(DateTime)`)
`dateUtc` query parameter with HTTP 400, and MUST NOT substitute the current day.

#### Scenario: Preview rejects a missing date

- GIVEN a request to `GET /api/DailyClosure/expected-totals` with `dateUtc` omitted or left at its default value
- WHEN `GetExpectedTotals` validates the query parameter
- THEN the response MUST be HTTP 400
- AND no expected totals MUST be computed or returned
