using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using StateleSSE.AspNetCore;
using StateleSSE.Backplane.Redis.Infrastructure;

namespace StateleSSE.Backplane.Redis.Extensions;

/// <summary>
/// Extension methods for configuring RedisBackplane.SSE services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Redis-based SSE backplane to the service collection.
    /// Enables horizontal scaling of Server-Sent Events across multiple server instances.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Configuration action for backplane options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddRedisSseBackplane(
        this IServiceCollection services,
        Action<RedisSseBackplaneOptions> configure)
    {
        var options = new RedisSseBackplaneOptions();
        configure(options);

        // Register Redis connection multiplexer as singleton
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var config = ConfigurationOptions.Parse(options.RedisConnectionString);
            return ConnectionMultiplexer.Connect(config);
        });

        // Register RedisBackplane as singleton
        services.AddSingleton(sp =>
        {
            var redis = sp.GetRequiredService<IConnectionMultiplexer>();
            return new Infrastructure.RedisBackplane(redis, options.ChannelPrefix);
        });

        // Register ISseBackplane interface
        services.AddSingleton<ISseBackplane>(sp => sp.GetRequiredService<RedisBackplane>());

        return services;
    }

    /// <summary>
    /// Adds Redis-based SSE backplane with an existing IConnectionMultiplexer.
    /// Use this if you already have a configured Redis connection.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="channelPrefix">Channel prefix for Redis pub/sub (default: "backplane")</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddRedisSseBackplane(
        this IServiceCollection services,
        string channelPrefix = "backplane")
    {
        services.AddSingleton(sp =>
        {
            var redis = sp.GetRequiredService<IConnectionMultiplexer>();
            return new Infrastructure.RedisBackplane(redis, channelPrefix);
        });

        // Register ISseBackplane interface
        services.AddSingleton<ISseBackplane>(sp => sp.GetRequiredService<RedisBackplane>());

        return services;
    }
}

/// <summary>
/// Configuration options for Redis SSE backplane
/// </summary>
public class RedisSseBackplaneOptions
{
    /// <summary>
    /// Redis connection string (e.g., "localhost:6379")
    /// </summary>
    public string RedisConnectionString { get; set; } = "localhost:6379";

    /// <summary>
    /// Channel prefix for Redis pub/sub messages (default: "backplane")
    /// Multiple backplanes can coexist with different prefixes
    /// </summary>
    public string ChannelPrefix { get; set; } = "backplane";

    /// <summary>
    /// Keepalive interval for SSE connections (default: 30 seconds)
    /// Prevents ANCM timeout (120s) in IIS/Azure deployments
    /// </summary>
    public TimeSpan KeepaliveInterval { get; set; } = TimeSpan.FromSeconds(30);
}
