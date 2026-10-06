# Archive Report: audit-critical-remediation

**Change**: 2026-10-05-audit-critical-remediation
**Date**: 2026-10-05 (archive entry date)
**Store**: openspec (repo-local)
**Verdict**: PASS_WITH_WARNINGS (final independent verification; 0 blockers)

## Summary

Remediation of the 5 verified critical findings of the integral audit
(`docs/auditoria_integral_fases_1_4.txt`): SEC-01 server-authoritative rounding (adjustment
recomputed from persisted payments at completion, client value ignored); SEC-02 cash-advance
price guard on sale item mutations (coordinator flow untouched); SRE-01 deterministic
stock-deduction order (resolve targets, consolidate by `(target, reason, saleId)`, execute
ascending by target product id); SRE-02 idempotency (required `Idempotency-Key` on
items/transaction/closure with replay + 422 semantics, idempotent cancellation, unique
`ClosureDate` index plus in-transaction duplicate guard); CLEAN-02 cart commit resilience
(`ClientStateLogger` logging, server re-sync, exact operator messages, no silent swallows).
The 15 remaining confirmed findings were documented as roadmap in the proposal for later
changes.

## Implementation Slices

| Slice | Scope | Commit |
|-------|-------|--------|
| Plan | SDD plan (6 slices + closure) + ANEXO 8.149 | `e779bbe` |
| 1 (T1) | Deterministic stock deduction (SRE-01): pure consolidator + ordered batch | `17f86f4` |
| 2 (T2) | Server-authoritative rounding in checkout (SEC-01) | `13fb59a` |
| 3 (T3) | Cash-advance price guard on item mutations (SEC-02) | `d1f5c57` |
| 4 (T4) | Backend idempotency: shared resolver, keys on items/transaction/closure, idempotent cancel, unique `ClosureDate` index (SRE-02 backend) | `92e980c` |
| 5 (T5) | WPF + Web clients send `Idempotency-Key` (SRE-02 clients) | `036f6e6` |
| 6 (T6) | Cart commit resilience (CLEAN-02) | `8445bcc` |
| Closure | Final verification + ANEXO 8.149 + tracker close | `f566280` + registration `cf556c8` |

## Verify Report (Final State)

| Metric | Value |
|--------|-------|
| Verdict | PASS WITH WARNINGS — 0 blockers |
| Requirements | 9/9 with pinned evidence |
| Scenarios | 19 (18 COMPLIANT + 1 PARTIAL — S3 cent drift preview vs persisted, recorded) |
| Build | 0 errors / 0 warnings |
| Suite at close | 1814/1814 |
| Coverage (final) | Core 0.8835 / Sales.Module 0.8911 / Inventory.Module 0.8558 (exit 0) |
| Web | 308/308 + `npm run lint` clean |
| Gated E2E | smoke 1/1, sale 1/1, cash closure 1/1 (real keyed client-server contract + fresh-DB migration) |

**Archive-time task reconciliation (recorded exception)**: the persisted SDD `tasks.md` was
reconciled (all unchecked → checked) by the orchestrator at T7 closure under the Task Completion
Gate exception. At archive time the artifact was verified to contain 18 checked checkbox tasks,
0 unchecked. Completion is proven by the final verify-report, the ODD tracker rows with commits
(`odd/tasks/auditoria-criticos-remediacion.md` L4–L10), and the nine committed slices.

## Specs Synced

| Domain | Action | Result |
|--------|--------|--------|
| `checkout-integrity` | Created | 2 requirements / 6 scenarios |
| `stock-deduction-concurrency` | Created | 1 requirement / 3 scenarios |
| `api-idempotency` | Created | 4 requirements / 6 scenarios |
| `cart-commit-resilience` | Created | 2 requirements / 4 scenarios |

Composition deviation (recorded): `gentle-ai sdd-archive-compose` is absent in the installed
build `4.0.1-0.20261003190532-0dda8895f664` (verified: `unknown command "sdd-archive-compose"`,
exit 1); composition ran as a deterministic PowerShell script with decode/encode roundtrip
checks, canonical header-shape assertions, and requirement/scenario count equality
(2/6, 1/3, 4/6, 2/4). Byte-level extracted-section SHA256 equality between each delta (first
`### Requirement:` → EOF) and its canonical file was asserted per domain: checkout-integrity
`2109204D2FDF345998AFFC09B731AE40F522152075C362BD55611100DE3624B2`, stock-deduction-concurrency
`129821C9873699A41E90DCBFC7C375A0D022746228198EA6F572B2001E4F3691`, api-idempotency
`A5F64B07BDC411FCF0AC2832C1AEF1A01922ACE318961E35686D1BDD8556270F`, cart-commit-resilience
`6E7ECF463EDF3629041603949155F4B0DDA36461D34E8D519B99031F082EDE42`. All four domains are NEW;
0 REMOVED requirements.

## Archive Notes

- Mechanical move: pre-move recursive snapshot + per-file SHA256 readback → 0 differences
  (10/10 files byte-identical). `git mv` staged 9 renames plus 1 rename-with-modification
  (`state.yaml` `next:` line updated pre-move per archive step 2).
- Native status/compose CLIs are absent in this build (`gentle-ai sdd-status` and
  `sdd-archive-compose` both unknown commands); archive readiness was carried by the
  orchestrator's structured final-state facts (verify PASS WITH WARNINGS, all slices committed).
- Residuals preserved: closure uniqueness is timestamp-level (not calendar-day); preview↔persisted
  cent drift (S3 PARTIAL); migration `20261005120000_AddUniqueClosureDateIndex` pending on dev DB
  `CommandCenterDb` (applies at next boot; 51 closures / 0 duplicate dates verified); resolver not
  DI-registered (parent-approved Option 2); T4 RED mutation-gated; Web modal tests structural
  (runner has no DOM); Web has no `/api/dailyclosure` caller (uses `/api/shifts/close`, out of
  keyed scope); `dotnet ef` broken in this environment (migration state verified via psql).
- ANEXO 8.149 registered in `docs/reporte.txt` (L12259).
