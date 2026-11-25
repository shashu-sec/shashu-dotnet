# Changelog — SHASHU .NET SDK

This project adheres to [Semantic Versioning](https://semver.org/).

## [1.1.1] — 2026-11-25

Fixed: when the gateway was busy and the SDK retried, the retry could be sent
without the retrieval declaration. The request (body, headers, and
`shashu_metadata`) is now built once per logical call and only re-sent.
`ShashuClient` does not auto-join `declareRetrieval` (attach via
`ShashuRoutingHandler`).

Fixed: requests routed through ShashuRoutingHandler did not send the SHASHU
trace and session headers.

## [1.1.0] — 2026-11-25

Wire names moved to SHASHU (requires a SHASHU gateway); key prefix `tg_sk_` unchanged.

Retries when the gateway says busy (Retry-After + jitter, default 2 retries, configurable).

The one-shot `pass_expired` retry is unchanged and does not stack with busy-retry.
`x-shashu-hop-ticket` is not sent (never sent in 1.0.0). Planned later.

### Breaking

- Environment variables are `SHASHU_API_KEY` / `SHASHU_BASE_URL` / `SHASHU_DISABLE` only. The `TRUSTGATE_*` fallbacks are removed.
- Wire names are SHASHU only. Old TrustGate names are neither sent nor read.

## [1.0.0] — 2026-11-13

First public SHASHU release (`Shashu.Sdk` 1.0.0, Apache-2.0, **net10.0**).

- Key sign-in with a `tg_sk_` agent key, as before.
- Cloud pass: `ShashuConfig.TokenProvider`. The pass is cached until 60 seconds
  before JWT `exp`. On HTTP 401 `pass_expired` the SDK fetches a fresh pass and
  retries once, before any streamed byte. Other `pass_*` codes raise
  `ShashuAuthenticationException` with `.Code` and the gateway's sentence.
- Environment variables are `SHASHU_*` first, then `TRUSTGATE_*`.
- User-Agent is `shashu-dotnet/1.0.0`.

Earlier 0.x alphas used previous product names. This 1.0.0 is the first SHASHU
package.
