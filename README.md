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
<PackageReference Include="Shashu.Sdk" Version="1.3.0" />
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

## Send a file or image

A message can carry an ordered list of parts instead of one string. The SDK does
the base64 encoding.

```csharp
var statement = await File.ReadAllBytesAsync("statement.pdf");
await using var client = new ShashuClient(new ShashuConfig
{
    BaseUrl = new Uri("https://gw.shashu.example"),
    ApiKey = "shashu_sk_…",
});
var response = await client.Chat.Completions.CreateAsync(new ChatCompletionRequest
{
    Model = "gpt-4o",
    Messages =
    [
        ChatMessage.WithParts(
            "user",
            [
                ContentPart.Text("Summarise this statement."),
                ContentPart.File(statement, "statement.pdf", "application/pdf"),
            ]),
    ],
});
Console.WriteLine(response.Choices[0].Message.Content);
```

`ContentPart.ImageData(bytes, "image/png")` attaches an image from bytes and
`ContentPart.ImageUrl(url)` refers to one by URL.

Your gateway decides which file types and sizes it accepts and what it does with
a file it will not pass; the SDK adds no limits. A refusal arrives as the same
exceptions as any other.

## One trace for a run of calls

Each call made outside a span starts its own trace. To keep a run of calls (a
conversation, an agent run) in one trace, make them inside `Tracer.Span()`
(`Shashu.Tracing`):

```csharp
using (Tracer.Span("support-conversation"))
{
    await client.Chat.Completions.CreateAsync(turn1);
    await client.Chat.Completions.CreateAsync(turn2);
}
```

Every call inside the block sends the same `x-shashu-trace-id`, including
across `await`. Trace ids the SDK creates are 32 lowercase hex characters. A
trace id you bring in, for example from an incoming request with
`Tracer.Incoming(headers)`, is sent unchanged.

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
