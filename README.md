# StateleSSE.Backplane.Redis

Redis-based backplane for horizontally scaling SSE across multiple servers.

## Installation

```bash
dotnet add package StateleSSE.Backplane.Redis
```

## Setup

```csharp
using StateleSSE.Backplane.Redis.Infrastructure;
using StackExchange.Redis;

var redis = ConnectionMultiplexer.Connect("localhost:6379");
builder.Services.AddSingleton<ISseBackplane>(sp =>
    new RedisBackplane(redis, channelPrefix: "myapp"));
```

## Usage

```csharp
using StateleSSE.AspNetCore;

[HttpGet("events")]
public async Task StreamEvents([FromQuery] string gameId)
{
    var channel = ChannelNamingExtensions.Channel<PlayerJoinedEvent>("game", gameId);
    await HttpContext.StreamSseAsync<PlayerJoinedEvent>(backplane, channel);
}

public async Task PublishEvent(string gameId)
{
    await backplane.PublishToGroup($"game:{gameId}:PlayerJoinedEvent", evt);
}
```

## How It Works

Events published via `PublishToGroup()` are broadcast to Redis pub/sub. All server instances receive the message and forward to their local SSE clients.

## Configuration

```csharp
var options = ConfigurationOptions.Parse("localhost:6379");
options.AbortOnConnectFail = false;
options.ReconnectRetryPolicy = new ExponentialRetry(5000);

var redis = ConnectionMultiplexer.Connect(options);
new RedisBackplane(redis, channelPrefix: "prod");
```

## Diagnostics

```csharp
var diagnostics = backplane.GetDiagnostics();
Console.WriteLine($"Groups: {diagnostics.TotalGroups}");
Console.WriteLine($"Subscribers: {diagnostics.TotalLocalSubscribers}");
```

## License

MIT
