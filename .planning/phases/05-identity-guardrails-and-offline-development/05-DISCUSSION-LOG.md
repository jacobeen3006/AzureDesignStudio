# Phase 5: Identity, Guardrails, and Offline Development - Discussion Log

> Audit trail only. Not read by downstream agents.

**Date:** 2026-11-14

## Personal accounts
Options: owner of own tenant scope / read-only unless a role is granted / configurable per deployment. **Chosen: owner of their own tenant scope.**

## Self-approval
Options: always refuse / tenant setting to allow it / allowed only in personal scope. **Chosen: tenant setting to allow it (default off, audited).**

## Anonymous RPCs
Options: only Cost.GetRetailPrices (recommended) / nothing anonymous. **Chosen: only Cost.GetRetailPrices.**

## Decided autonomously
[auto] [Identity] - Q: "class layout" -> Selected: "Server/Auth with pure parsing" (reversible technical - local refactor)
[auto] [Identity] - Q: "in-band 401 shape" -> Selected: "reuse existing in-band status convention" (reversible technical - additive if a field is needed)
[auto] [Crypto] - Q: "DEBUG key storage" -> Selected: "Data Protection file key ring, development value only" (reversible technical - behind the seam)
[auto] [Tenancy] - Q: "tenant plumbing" -> Selected: "extend UserState caller" (reversible technical - internal)
[auto] [Testing] - Q: "isolation tests" -> Selected: "Server.Tests with claim-emitting test handler" (reversible technical - test-local)
