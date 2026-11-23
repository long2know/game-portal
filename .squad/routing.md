# Work Routing

Route Jade Court portal work using the [active roster](team.md) and [project decisions](decisions.md).

## Routing Table

| Work Type | Route To | Examples |
|-----------|----------|----------|
| Architecture and cross-cutting changes | Lead | Engine and service boundaries, compatibility, scope, design and release-readiness review |
| Game rules, engines and saves | Rules | `src/JadeCourt.*` and `src/Mahjong.Rules/`: deterministic moves, scoring, bot legality, versioned checkpoints and engine tests |
| API, realtime and persistence | Backend | `src/Mahjong.Server/`: mount-aware HTTP/WebSocket, guest and invite authorization, SQLite migrations and resume |
| Browser UI, accessibility and art | Frontend | `web/src/` and `web/e2e/`: responsive play, input, reduced motion, asset provenance and cold-load budgets |
| Build, CI and release | Infra | `Dockerfile`, Compose, `scripts/`, `.github/workflows/ci.yml`: reproducibility, isolated deployment, tested-image promotion and rollback |

## Support

Scribe consolidates public-safe decisions. Rai and Fact Checker review under
their [safety](rai/policy.md) and [verification](fact-checker/policy.md) policies.
Ralph checks work only when explicitly requested; no background watcher is configured.

## Issue Routing

When asked to triage, Lead proposes an owner using the table. `squad` and
`squad:{name}` labels are routing hints, not triggers to assign or start work.
Do not change remote issues, PRs or labels, or enable automation, without
maintainer authorization.
