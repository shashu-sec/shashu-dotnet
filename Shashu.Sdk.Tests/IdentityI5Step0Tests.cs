using Shashu;
using Xunit;

namespace Shashu.Tests;

/// <summary>
/// IDENTITY I5 step 0 — prove today's SDK cannot take a cloud pass.
/// Fails on the import: there is no TokenProvider option, and Authorization
/// is baked into HttpClient.DefaultRequestHeaders at construction.
/// </summary>
public class IdentityI5Step0Tests
{
    [Fact]
    public void Config_has_TokenProvider()
    {
        Assert.NotNull(typeof(ShashuConfig).GetProperty("TokenProvider"));
    }

    [Fact]
    public void HttpClient_does_not_bake_Authorization_at_construction()
    {
        using var http = new HttpClient();
        var client = new ShashuClient(
            new ShashuConfig
            {
                BaseUrl = new Uri("http://127.0.0.1:9"),
                ApiKey = "shashu_sk_test",
            },
            http);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        _ = client;
    }
}
