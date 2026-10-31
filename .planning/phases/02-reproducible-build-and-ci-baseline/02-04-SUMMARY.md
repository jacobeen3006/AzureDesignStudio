# Plan 02-04 Summary

## Probes
- Probe A (gate blocks): rc=1, `error NU1903: Warning As Error: Package 'AutoMapper' 12.0.1 ... GHSA-rvv3-g6hj-g44x`; no expiry failure in log.
- Probe B (exception is the only cover): rc=0, no NU1903.
- Probe C (expiry fires): rc=1, `AutoMapper audit suppression expired on 2020-01-01 (advisory GHSA-rvv3-g6hj-g44x)... Today is 2026-10-31.`; unmodified build rc=0; no MSB4086.
- Probe D: NuGetAuditLevel=high, NuGetAuditMode=all, exactly one NuGetAuditSuppress item.

## Commits
- 60fce08 build(02-04): ...
- be4a60e docs(02-04): mark BUILD-04 complete...
