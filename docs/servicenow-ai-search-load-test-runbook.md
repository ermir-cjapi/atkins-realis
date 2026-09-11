# ServiceNow AI Search non-production load-test runbook

## Safety boundary

This test calls the ServiceNow AI Search API directly. It is manual, opt-in, excluded from normal build/deploy pipelines, and must never target production. Do not run it until the ServiceNow owner has supplied written approval for the endpoint, window, synthetic identity/query, maximum RPS, maximum concurrency, and abort thresholds.

The script:

- acquires OAuth once during setup;
- performs no client retries;
- refuses hosts whose names do not contain `dev`, `uat`, or `test`;
- refuses to start unless `SERVICENOW_LOAD_TEST_APPROVED=true`;
- refuses a target above the explicit approved RPS ceiling;
- emits only aggregate metrics and never writes tokens, UPNs, or query content to the result artifact.

## Approval record

Complete this table before execution and link the written approval.

| Item | Approved value |
|---|---|
| ServiceNow owner/contact | Pending |
| Engineering operator/contact | Pending |
| Written approval reference | Pending |
| Non-production endpoint | Pending |
| Test date and window | Pending |
| Synthetic UPN/query data | Pending |
| Approved maximum RPS | Pending |
| Approved maximum VUs/concurrency | Pending |
| Error-rate abort threshold | Pending |
| 429-count abort threshold | Pending |
| Other owner abort conditions | Pending |

The application default of 5 RPS with burst 5 is conservative and provisional. It is not an agreed ServiceNow SLA or test approval.

## Inputs

Set these environment variables locally or in an approval-gated secret-capable test environment:

| Variable | Purpose |
|---|---|
| `SERVICENOW_LOAD_TEST_APPROVED=true` | Explicit execution gate |
| `SERVICENOW_SEARCH_ENDPOINT` | Full non-production AI Search path |
| `SERVICENOW_TOKEN_ENDPOINT` | Non-production OAuth endpoint |
| `SERVICENOW_CLIENT_ID` | Protected OAuth client ID |
| `SERVICENOW_CLIENT_SECRET` | Secret; never persist in scripts/artifacts |
| `SERVICENOW_SYNTHETIC_UPN` | Owner-approved synthetic identity |
| `SERVICENOW_SYNTHETIC_QUERY` | Owner-approved synthetic query |
| `SERVICENOW_TARGET_RPS` | Controlled requested arrival rate |
| `SERVICENOW_APPROVED_MAX_RPS` | Written-approval ceiling |
| `SERVICENOW_APPROVED_MAX_VUS` | Written-approval concurrency ceiling |
| `SERVICENOW_TEST_DURATION` | Steady-state duration, such as `5m` |
| `SERVICENOW_ABORT_ERROR_RATE` | Optional; defaults to `0.05` |
| `SERVICENOW_ABORT_429_COUNT` | Optional; defaults to `10` |

Run the test manually with k6 against `tests/load/servicenow-ai-search.js`. Do not enable HTTP debug output because request URLs contain scoped synthetic inputs and headers contain a bearer token.

## Acceptance and abort criteria

Provisional performance targets are:

- p95 below 2 seconds;
- p99 below the 3-second application turn budget;
- no dropped iterations;
- error and 429 rates within the ServiceNow-owner-approved thresholds.

Stop immediately for production-host ambiguity, unexpected real-user data, sustained 429s, owner request, platform instability, or an approval ceiling mismatch. The script automatically aborts when configured metric thresholds fail.

## Redacted result record

Archive only the generated aggregate summary and an operator-authored record containing:

- approval reference, non-secret test configuration, window, and script commit;
- requested and achieved RPS;
- p50/p95/p99;
- timeout, error, 429, and dropped-iteration counts/rates;
- aborts or operational observations;
- ServiceNow-owner conclusion and recommended application limit.

Do not archive environment-variable dumps, access tokens, credentials, authorization headers, full URLs, synthetic UPNs, query content, or response bodies.

After owner agreement, update the environment-specific application rate settings only when they differ from safe application defaults. Record the approved ceiling and test evidence here; do not describe the resulting setting as an SLA unless the owner explicitly does so.
