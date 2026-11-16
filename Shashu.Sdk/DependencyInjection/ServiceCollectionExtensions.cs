using Microsoft.Extensions.DependencyInjection;

namespace Shashu.DependencyInjection;

/// <summary>
/// ASP.NET Core / generic-host integration for Shashu. Registers a
/// singleton <see cref="ShashuClient"/> that uses
/// <c>IHttpClientFactory</c> under the hood — the .NET-recommended
/// pattern for HttpClient lifetime management (avoids socket exhaustion
/// on long-running services).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="ShashuClient"/> + its options.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddShashu(opts => {
    ///     opts.ApiKey  = builder.Configuration["Shashu:ApiKey"];
    ///     opts.BaseUrl = new Uri(builder.Configuration["Shashu:BaseUrl"]!);
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddShashu(
        this IServiceCollection services,
        Action<ShashuConfig> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddHttpClient("Shashu");
        services.AddSingleton(provider =>
        {
            var opts = new ShashuConfig();
            configure(opts);
            var factory = provider.GetRequiredService<IHttpClientFactory>();
            return new ShashuClient(opts, factory.CreateClient("Shashu"));
        });
        return services;
    }
}
