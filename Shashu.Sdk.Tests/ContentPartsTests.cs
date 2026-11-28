using System.Net;
using System.Text;
using System.Text.Json;
using Shashu;
using Shashu.Chat;
using Shashu.Routing;
using Shashu.Tests.Harness;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// Multi-part messages. The <c>*.python-node.json</c> goldens are the exact
/// request bodies the Python (shashu-ai 1.2.0) and Node (@shashu-ai/sdk 1.2.0)
/// SDKs sent for the same input; the two were byte-identical.
/// </summary>
public class ContentPartsTests
{
    private const string Text = "Résumé — a+b <x> & 'q' \"d\" / \\ 😀\n\tend";

    private static readonly byte[] Pdf = Encoding.Latin1.GetBytes("%PDF-1.4\nûï¾ÿ?>~ golden");

    private static readonly byte[] Png =
        Encoding.Latin1.GetBytes("\u0089PNG\r\n\u001a\n\u0000ûÿþ");

    private sealed class Capture : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _plan;

        public List<byte[]> Bodies { get; } = new();

        public List<string> Authorizations { get; } = new();

        public Capture(params Func<HttpResponseMessage>[] plan) => _plan = new(plan);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? "");
            return _plan.Count > 0 ? _plan.Dequeue()() : Ok();
        }
    }

    [Fact]
    public async Task Text_and_file_match_Python_and_Node_bytes()
    {
        var capture = new Capture();
        await Send(capture, TextAndFile());
        Assert.Equal(Golden("text_file"), Encoding.UTF8.GetString(capture.Bodies[0]));
    }

    [Fact]
    public async Task Image_data_uri_matches_Python_and_Node_bytes()
    {
        var capture = new Capture();
        await Send(capture, Request(
            ChatMessage.WithParts("user", [ContentPart.Text("look"), ContentPart.ImageData(Png, "image/png")])));
        Assert.Equal(Golden("text_image_data"), Encoding.UTF8.GetString(capture.Bodies[0]));
    }

    [Fact]
    public async Task Image_https_url_matches_Python_and_Node_bytes()
    {
        var capture = new Capture();
        await Send(capture, Request(
            new ChatMessage("system", "sys"),
            ChatMessage.WithParts(
                "user",
                [ContentPart.Text("look"), ContentPart.ImageUrl("https://example.com/a+b.png?x=1&y=%20")])));
        Assert.Equal(Golden("text_image_url"), Encoding.UTF8.GetString(capture.Bodies[0]));
    }

    [Fact]
    public async Task Busy_retry_resends_identical_parts_with_the_declaration_joined()
    {
        var capture = new Capture(Busy);
        Retrieval.DeclareRetrieval(new[] { "g6 declared document" }, corpus: "g6_corpus", query: "g6 query");
        await Send(capture, TextAndFile());
        Assert.Equal(2, capture.Bodies.Count);
        Assert.Equal(capture.Bodies[0], capture.Bodies[1]);
        using var body = JsonDocument.Parse(capture.Bodies[0]);
        var sent = Encoding.UTF8.GetString(capture.Bodies[0]);
        Assert.Contains("g6_corpus", sent);
        Assert.Contains("g6 declared document", sent);
        var parts = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(Text, parts[0].GetProperty("text").GetString());
        Assert.Equal(
            "data:application/pdf;base64," + Convert.ToBase64String(Pdf),
            parts[1].GetProperty("file").GetProperty("file_data").GetString());
    }

    [Fact]
    public async Task Pass_expired_refresh_keeps_the_same_parts()
    {
        var capture = new Capture(() => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                "{\"error\":{\"message\":\"This pass has expired. Fetch a new one.\","
                + "\"type\":\"invalid_request_error\",\"code\":\"pass_expired\"}}",
                Encoding.UTF8,
                "application/json"),
        });
        var first = PassMint.Mint(300);
        var second = PassMint.Mint(300, first.Iat + 1);
        var tokens = new[] { first.Token, second.Token };
        var calls = 0;
        using var http = new HttpClient(capture) { BaseAddress = TestGateway.Uri };
        using var client = new ShashuClient(
            new ShashuConfig
            {
                BaseUrl = TestGateway.Uri,
                TokenProvider = _ => new ValueTask<string>(tokens[Math.Min(calls++, 1)]),
                MaxRetries = 0,
            },
            http);

        await client.Chat.Completions.CreateAsync(TextAndFile());

        Assert.Equal(2, capture.Bodies.Count);
        Assert.NotEqual(capture.Authorizations[0], capture.Authorizations[1]);
        Assert.Equal(Golden("text_file"), Encoding.UTF8.GetString(capture.Bodies[0]));
        Assert.Equal(capture.Bodies[0], capture.Bodies[1]);
    }

    [Fact]
    public async Task A_reply_with_array_content_is_read_as_parts()
    {
        var capture = new Capture(() => Reply(
            "[{\"type\":\"text\",\"text\":\"hello\"},{\"type\":\"file\",\"file\":{\"file_id\":\"f\"}}]"));
        var message = (await Send(capture, TextAndFile())).Choices[0].Message;
        Assert.Null(message.Content);
        Assert.Equal(2, message.ContentParts!.Count);
        Assert.Equal("hello", message.ContentParts[0].TextValue);
        Assert.Equal("file", message.ContentParts[1].Type);
        Assert.Null(message.ContentParts[1].TextValue);
        Assert.Equal("{\"type\":\"file\",\"file\":{\"file_id\":\"f\"}}", message.ContentParts[1].ToJson());
    }

    [Fact]
    public async Task A_text_reply_is_read_exactly_as_before()
    {
        var message = (await Send(new Capture(), TextAndFile())).Choices[0].Message;
        Assert.Equal("ok", message.Content);
        Assert.Null(message.ContentParts);
    }

    [Fact]
    public void Equality_with_and_to_string()
    {
        ContentPart[] parts = [ContentPart.Text("t"), ContentPart.File(Pdf, "a.pdf", "application/pdf")];
        var message = ChatMessage.WithParts("user", parts);
        Assert.Null(message.Content);
        Assert.Equal(parts[1], ContentPart.File(Pdf, "a.pdf", "application/pdf"));
        Assert.Equal(parts[1].GetHashCode(), ContentPart.File(Pdf, "a.pdf", "application/pdf").GetHashCode());
        Assert.NotEqual(parts[0], parts[1]);
        // A debug string must never carry the attached bytes.
        Assert.DoesNotContain(Convert.ToBase64String(Pdf), message.ToString() + parts[1]);

        // Setting text on a copy replaces the parts.
        var replaced = message with { Content = "plain" };
        Assert.Null(replaced.ContentParts);
        Assert.Equal(new ChatMessage("user", "plain"), replaced);
        Assert.Equal(2, (message with { Name = "n" }).ContentParts!.Count);

        // The list is copied.
        var list = new List<ContentPart> { parts[0] };
        var copied = ChatMessage.WithParts("user", list);
        list.Add(parts[1]);
        Assert.Single(copied.ContentParts!);
    }

    [Fact]
    public void A_callers_own_serializer_round_trips_parts()
    {
        var message = ChatMessage.WithParts("user", [ContentPart.Text("a+b 😀")]);
        var json = JsonSerializer.Serialize(message);
        Assert.Equal(
            "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"a+b 😀\"}]}", json);
        var back = JsonSerializer.Deserialize<ChatMessage>(json)!;
        Assert.Equal(message.ContentParts, back.ContentParts);
    }

    [Fact]
    public void Control_characters_and_lone_surrogates_are_escaped_like_JSON_stringify()
    {
        var json = ContentPart.Text("\u0001\u001f\b\f\r" + '\ud800' + "x\u2028").ToJson();
        Assert.Equal(
            "{\"type\":\"text\",\"text\":\"\\u0001\\u001f\\b\\f\\r\\ud800x" + '\u2028' + "\"}", json);
    }

    [Fact]
    public void Factories_refuse_missing_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => ContentPart.Text(null!));
        Assert.Throws<ArgumentNullException>(() => ContentPart.ImageUrl(null!));
        Assert.Throws<ArgumentNullException>(() => ContentPart.ImageData(null!, "image/png"));
        Assert.Throws<ArgumentException>(() => ContentPart.ImageData(Png, " "));
        Assert.Throws<ArgumentException>(() => ContentPart.File(Pdf, "", "application/pdf"));
        Assert.Throws<ArgumentException>(() => ContentPart.File(Pdf, "a.pdf", ""));
        Assert.Throws<ArgumentException>(() => ChatMessage.WithParts("user", []));
        Assert.Throws<ArgumentException>(() => ChatMessage.WithParts("user", [null!]));
    }

    private static ChatCompletionRequest TextAndFile() => Request(ChatMessage.WithParts(
        "user", [ContentPart.Text(Text), ContentPart.File(Pdf, "q3 réport+.pdf", "application/pdf")]));

    private static ChatCompletionRequest Request(params ChatMessage[] messages) =>
        new() { Model = "gpt-4o-mini", Messages = messages };

    private static async Task<ChatCompletionResponse> Send(Capture capture, ChatCompletionRequest request)
    {
        using var http = new HttpClient(capture) { BaseAddress = new Uri("https://test.invalid/") };
        using var client = new ShashuClient(
            new ShashuConfig { BaseUrl = TestGateway.Uri, ApiKey = "shashu_sk_golden" }, http);
        return await client.Chat.Completions.CreateAsync(request);
    }

    private static string Golden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name + ".python-node.json"));

    private static HttpResponseMessage Ok() => Reply("\"ok\"");

    private static HttpResponseMessage Reply(string contentJson) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"id\":\"x\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
            + contentJson + "},\"finish_reason\":\"stop\"}]}",
            Encoding.UTF8,
            "application/json"),
    };

    private static HttpResponseMessage Busy()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":{\"message\":\"busy\",\"code\":\"gateway_busy\"}}"),
        };
        response.Headers.TryAddWithoutValidation("Retry-After", "0");
        response.Headers.TryAddWithoutValidation("x-should-retry", "true");
        return response;
    }
}
