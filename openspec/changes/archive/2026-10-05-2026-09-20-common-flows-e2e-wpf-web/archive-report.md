# Archive Report: common-flows-e2e-wpf-web

**Change**: 2026-09-20-common-flows-e2e-wpf-web
**Date**: 2026-10-05 (archive entry date)
**Store**: openspec (repo-local)
**Verdict**: INTENTIONAL PARTIAL ARCHIVE (legacy pre-convention change; specs not synced)

## Summary

Legacy E2E change shipped as work item 8.142: WPF `AutomationId` alignment across the desktop views,
FlaUI E2E suites for the common flows (POS sale, pending orders, pending pickups, cash drawer, inventory,
sales history, daily closure, and WPF-independent flows), web Playwright parity for catalog/inventory,
and installer publish outputs (Backend.API, Desktop.Client, UpdaterService).

## Final State

| Metric | Value |
|--------|-------|
| Tasks | 28/28 checkbox tasks checked, 0 unchecked (`tasks.md`, 39 lines total) |
| Shipped in | `a98a56721a86116ec10bf3e17018c500ba5bdc20` — `test(8.142): amplia E2E WPF/web (flujos comunes y venta/hold/pickups con mock HTTP y helpers) + excluye secrets.json del publish` |
| State artifact | Absent (pre-convention change: no `state.yaml`, no `verify-report.md`) |

## Intentional Partial Archive (Recorded)

- **Specs NOT synced to canonical.** The change predates the delta-spec convention (created 2026-09-20);
  its spec lives at `specs/common-flows-spec.md` in a non-delta, non-domain-aligned format. Converting it
  to `## ADDED/MODIFIED/REMOVED Requirements` domains would require model-driven rewriting, which the
  Mechanical Copy Contract forbids. The spec content is preserved verbatim inside this archived folder.
- **Tasks complete.** All checkbox tasks were verified checked (28/28, 0 unchecked) before the archive move.
- **Work shipped and merged** in commit `a98a567` (8.142).
- **Authorization**: maintainer mandate of 2026-10-05, verbatim:
  `"Verifica riesgos y problemas residuales abiertos y registados, de existir ciérralos"`.

## Archive Notes

- Mechanical move: pre-move recursive snapshot + per-file SHA256 readback → 0 differences (4 files):
  - `design.md` len=2174 sha=3DCB8A64B238A3A0A998F6D937D92DB2A3D84C420A57FFD5837FE438F9298C55
  - `proposal.md` len=1735 sha=AE2ECC5FAC5D4CA35625CD02EEA3B2A914F86BB999AFB5564BE4B512B88449D9
  - `specs\common-flows-spec.md` len=2935 sha=BC0E94BA944FC5C7E5F4BC112A8CECCAEB1696326EC96473463C4BB7E040851A
  - `tasks.md` len=3684 sha=D24A586F07B32FD7A58AD2E5C937B4FFEE827FAC035B54D8D96A16EC9A8DDDCA
