# Archive Report: e2e-sales-hold-pickups

**Change**: 2026-09-20-e2e-sales-hold-pickups
**Date**: 2026-10-05 (archive entry date)
**Store**: openspec (repo-local)
**Verdict**: INTENTIONAL PARTIAL ARCHIVE (legacy pre-convention change; specs not synced)

## Summary

Legacy web E2E change shipped as part of work item 8.142: Playwright flows for authentication
(`loginAsAdmin` helper with password-rotation support), the complete POS sales flow, the On-Hold
order lifecycle (create, edit, checkout, cancel), and pending pickups confirmation.

## Final State

| Metric | Value |
|--------|-------|
| Tasks | 11/11 checkbox tasks checked, 0 unchecked (`tasks.md`, 20 lines total) |
| Shipped in | `a98a56721a86116ec10bf3e17018c500ba5bdc20` — `test(8.142): amplia E2E WPF/web (flujos comunes y venta/hold/pickups con mock HTTP y helpers) + excluye secrets.json del publish` |
| State artifact | Absent (pre-convention change: no `state.yaml`, no `verify-report.md`) |

## Intentional Partial Archive (Recorded)

- **Specs NOT synced to canonical.** The change predates the delta-spec convention (created 2026-09-20);
  its spec lives at `specs/e2e-sales-flow/spec.md` in a non-delta, non-domain-aligned format. Converting it
  to `## ADDED/MODIFIED/REMOVED Requirements` domains would require model-driven rewriting, which the
  Mechanical Copy Contract forbids. The spec content is preserved verbatim inside this archived folder.
- **Tasks complete.** All checkbox tasks were verified checked (11/11, 0 unchecked) before the archive move.
- **Work shipped and merged** in commit `a98a567` (8.142).
- **Authorization**: maintainer mandate of 2026-10-05, verbatim:
  `"Verifica riesgos y problemas residuales abiertos y registados, de existir ciérralos"`.

## Archive Notes

- Mechanical move: pre-move recursive snapshot + per-file SHA256 readback → 0 differences (4 files):
  - `design.md` len=2327 sha=3BD21BF30A9A25C2D641B8F75CD9F36562A3DAE3348B868761E854F84F705934
  - `proposal.md` len=3278 sha=6874A675F5C623B6422E4AE7B5A81578B546F5105A57E07B78B9E3CA9A362297
  - `specs\e2e-sales-flow\spec.md` len=3362 sha=487F1DAA8EBA31F3CC829C98F3654794ED8FBDE31F51E17B966EB84C79C44831
  - `tasks.md` len=1759 sha=1FCE5A47570C0E6D1DFE189F484D3F124BE5F2BDEF721811B73C2F0970181A3C
