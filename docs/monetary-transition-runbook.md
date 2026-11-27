# Monetary transition runbook (8.211-S11)

This procedure is the only supported way to change the monetary base. The application blocks
automatic mode/primary/secondary changes once financial data exists; it never reinterprets
historical balances. Presentation symbols remain editable online.

## What the guard blocks

The API rejects a profile `PUT` when the request changes `Mode`, `PrimaryCurrencyCode` or
`SecondaryCurrencyCode` and any of these tables has data:

- products (excluding the system cash-advance product)
- supplier invoices
- sales and sale payments
- cash-drawer sessions
- daily closures
- receivables
- petty-cash expenses

Changing only `PrimaryCurrencySymbol` or `SecondaryCurrencySymbol` is allowed. `VES` is the
canonical code; legacy presentation aliases (`Bs.S`, `BsS`) are not accepted as profile ISO
codes. Case differences such as `ves` normalize to the same pair and do not count as a change.

The exact rejection message is:

> No se puede cambiar el modo ni la moneda base porque ya existen datos financieros. La moneda
> base solo puede cambiarse en una instalación nueva o mediante una migración explícita.

If the guard cannot be read, the API fails closed with the verification message and persists
nothing.

## Supported paths

### Fresh installation (no financial data)

1. Open settings and read `GET /api/settings/monetary-setup-status`.
2. Confirm `canConfigureMonetaryProfile = true`.
3. Save the complete profile with `PUT /api/settings/monetary-profile`.
4. If the result is `Single`, verify the payment-method catalog was prepared in the primary
   currency before the first sale.
5. Record the chosen profile in the installation log.

### Existing installation (financial data present)

There is no in-app base transition. Do not edit `SystemSettings` or monetary snapshots directly.

Use an explicit offline migration:

1. Announce a maintenance window and stop sales, cash operations and integrations.
2. Close or reconcile the open cash drawer; do not leave an active session.
3. Back up the database and export the financial reports needed for reconciliation.
4. Decide the target profile and migration mapping with the maintainer. Historical rows keep
   their original currency snapshots; the migration must translate only future-facing
   configuration, never recompute historical amounts.
5. Run the approved migration in a staging copy first and reconcile totals, receivables, cash and
   tax reports against the backup.
6. Apply the migration in production, then verify:
   - `GET /api/settings/monetary-setup-status` reports the expected profile.
   - A test sale uses the expected payment-method currencies.
   - Receipts and closures show the persisted currency, not the active profile as a substitute.
   - Historical sales still show their original snapshot.
7. Keep the backup and migration evidence in the installation record.

### Symbol-only change

Symbols may be updated with financial data. Validate the preview in settings, save, and confirm
new receipts and screens use the new symbol without changing stored amounts.

## Failure handling

- `BaseLockMessage`: expected when data exists; do not retry or bypass. Follow the offline
  migration path.
- `VerificationMessage`: the guard could not be read; restore database access and retry.
- Alias rejection: send an ISO-3 code; `VES` is canonical.
- After any failed attempt, read the profile again and confirm no partial write occurred.

