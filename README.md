# SHASHU .NET SDK

.NET client for **SHASHU**, a self-hosted AI security gateway.
Point it at your own gateway. There is no default hostname: construction
raises a configuration error rather than guessing.

Wire names (`x-shashu-*`, `shashu_metadata`, `shashu-version`,
`shashu_sk_`) are protocol names on the SHASHU wire and are unchanged.

After this first mention, this document says **SHASHU**.

## Install

```sh
dotnet add package Shashu.Sdk
```

```xml
<PackageReference Include="Shashu.Sdk" Version="1.0.0" />
```

Requires **net10.0** (current LTS, supported to November 2028).

## Quick start

Key:

```csharp
await using var client = new ShashuClient(new ShashuConfig
{
    BaseUrl = new Uri("https://your-shashu-gateway.example"),
    ApiKey = "shashu_sk_...",
});
var response = await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
{
    Model = "gpt-4o",
    Messages = [new ChatMessage("user", "Hello")],
});
Console.WriteLine(response.Choices[0].Message.Content);
```

Cloud pass (you fetch the pass; SHASHU never calls Entra, Google or AWS):

```csharp
await using var client = new ShashuClient(new ShashuConfig
{
    BaseUrl = new Uri("https://your-shashu-gateway.example"),
    TokenProvider = GetPass,
});
```

Give either a key or a token provider, not both. Environment fallbacks:
`SHASHU_API_KEY` / `SHASHU_BASE_URL`, then `SHASHU_API_KEY` /
`SHASHU_BASE_URL`.

## Pass refusals

A cloud pass is a JWT sent as `Authorization: Bearer`. On HTTP 401 the SDK
reads `error.code`. It retries **once**, with a fresh pass, only on
`pass_expired`, and only before any streamed byte. Every other `pass_*` code
raises `ShashuAuthenticationException` with `.Code` and the gateway's
sentence, unchanged.

| Code | Sentence |
|---|---|
| `pass_provider_not_set_up` | No identity provider is set up for agents. |
| `pass_not_licensed` | Pass sign-in needs the Enterprise plan. |
| `pass_expired` | This pass has expired. Fetch a new one. |
| `pass_wrong_audience` | This pass is meant for a different audience. |
| `pass_wrong_issuer` | This pass comes from an unknown issuer. |
| `pass_unknown_agent` | No agent matches this pass. |

Constants live on `Shashu`.

## Configuration

| Option | Default | Env |
|---|---|---|
| `ApiKey` | exactly one of this or `TokenProvider` | `SHASHU_API_KEY` |
| `TokenProvider` | — | — |
| `BaseUrl` | **none** | `SHASHU_BASE_URL` |
| `Timeout` | 60s | — |
| `MaxRetries` | 2 | — |

`SHASHU_DISABLE=1` makes the client, routing handler, retrieval bus and
tracer no-ops.

## Docs

https://shashu.ai/docs

## License

Apache-2.0. See `LICENSE` and `NOTICE`.
