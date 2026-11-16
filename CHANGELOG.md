# Changelog — SHASHU .NET SDK

This project adheres to [Semantic Versioning](https://semver.org/).

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
