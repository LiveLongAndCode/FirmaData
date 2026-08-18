# Production-level microservices

The continuation of [`monolith-microservice_demo-level.md`](monolith-microservice_demo-level.md). That guide ends at
Phase 12 with five services behind a YARP gateway on Docker Compose, verified against the monolith
for response parity. This one takes that stack from "correct" to "operable in production":
distributed caching, horizontal scaling, asynchronous messaging, distributed tracing, centralised
logging, authentication, secrets management, per-service persistence, contract testing, chaos and
load testing, alerting, supply-chain hardening, and a production compose topology with a runbook.

**Read the base guide first.** This document assumes `solution_microservices/`,
`docker-compose.microservices.yml`, `FirmaData.ServiceDefaults` and the port map from Appendix A
already exist. Phase numbering continues from 13, and every phase here follows the same contract:
one goal, an explicit exit criterion, and a stack that still builds and runs if you stop there.

**Status: documentation only.** Nothing here is implemented in the repository.

---

## Contents

| Phase | Goal | Adds |
| --- | --- | --- |
| [13](#13-phase-13--distributed-cache-redis) | Cache shared across replicas | Redis |
| [14](#14-phase-14--horizontal-scaling) | More than one replica per service | Compose scaling, YARP load balancing |
| [15](#15-phase-15--asynchronous-messaging) | Work that must not block a request | RabbitMQ, MassTransit, a worker service |
| [16](#16-phase-16--distributed-tracing) | "Which hop was slow" answered per request | OTLP, Tempo |
| [17](#17-phase-17--centralised-logging) | One place to grep five services | Loki |
| [18](#18-phase-18--authentication-and-authorisation) | Not everyone may call the API | Keycloak, JWT at the gateway |
| [19](#19-phase-19--secrets-management) | No credentials in compose files | Docker secrets, then Vault |
| [20](#20-phase-20--quotas-and-backpressure) | One client cannot starve the rest | Partitioned rate limits, concurrency limits |
| [21](#21-phase-21--per-service-persistence) | Lookups become replayable | PostgreSQL, EF Core |
| [22](#22-phase-22--contract-testing-and-versioning) | Breaking changes caught before deploy | Pact, versioning policy |
| [23](#23-phase-23--chaos-and-load-testing) | Resilience settings proven, not assumed | Toxiproxy, k6 |
| [24](#24-phase-24--alerting-and-slos) | Someone is told before the user notices | Alertmanager, burn-rate alerts |
| [25](#25-phase-25--supply-chain-and-container-hardening) | The image is defensible | Trivy gate, SBOM, cosign, digest pinning |
| [26](#26-phase-26--production-topology-and-runbook) | It can be operated by someone who did not build it | Compose overrides, runbook |

Phases 13–17 are foundational and should be done in order. 18–21 are independent of each other.
22–26 depend on most of what precedes them.

### What stays out, even here

Compose is the orchestrator throughout, by choice — the point is to show what production *needs*,
not to teach Kubernetes. Four things therefore remain out of scope and should be named honestly in
any interview or design review:

* **Scheduling and self-healing across hosts.** Compose restarts a container; it does not reschedule
  it onto a different machine. That is the single strongest argument for Kubernetes/ECS/Container
  Apps, and nothing below substitutes for it.
* **Rolling deployment with automatic rollback.** §26 gets close with a manual procedure; a real
  orchestrator does it declaratively.
* **Multi-region and disaster recovery.** Single host, single region throughout.
* **A service mesh.** mTLS between services is mentioned in §18 and deliberately not implemented —
  at five services it is not worth the operational surface.

---

## 13. Phase 13 — Distributed cache (Redis)

**Goal:** the statistics cache is shared by every replica of the statistics service and survives its
restart, without giving up the speed of an in-process cache.

**Why now:** Phase 14 adds replicas. With `IMemoryCache` alone, three replicas mean three cold
caches, three times the load on api.statbank.dk, and a hit ratio that drops by roughly two-thirds
the moment you scale out. Redis before replicas, not after.

### 13.1 Two levels, not one

Replacing `IMemoryCache` with Redis outright would be a mistake: every cache hit becomes a network
round trip. The right shape is L1 in-process (fast, per-replica, short TTL) in front of L2 Redis
(shared, long TTL).

```
request → CachingIndustryStatisticsProvider (L1, IMemoryCache, 60s)
        → DistributedIndustryStatisticsProvider (L2, Redis, 24h)
        → StatbankClient → api.statbank.dk
```

L1's TTL is deliberately short: it exists to absorb bursts, not to be authoritative. A 60-second
window means an invalidation published in Phase 15 takes at most a minute to take effect everywhere,
which is well inside what annual statistics tolerate.

### 13.2 Make L1's TTL configurable

`CachingIndustryStatisticsProvider` hardcodes 24 hours. That was right when it was the only cache;
as L1 it needs to be 60 seconds. This is the one change to an existing library that this guide asks
for, and it is additive — the default keeps the monolith's behaviour.

`solution_microservices/src/Backend/FirmaData.Statbank/StatbankCacheOptions.cs`:

```csharp
namespace FirmaData.Statbank;

// Extracted from CachingIndustryStatisticsProvider's constants so the same decorator can serve as
// a 24h single-level cache (its original role) or a 60s L1 in front of Redis (its role once
// FirmaData.Caching.Redis is registered). Defaults are the original constants, so nothing changes
// for a caller that does not configure it.
public sealed class StatbankCacheOptions
{
    public const string SectionName = "StatbankCache";

    public TimeSpan PositiveDuration { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan NegativeDuration { get; set; } = TimeSpan.FromMinutes(5);
}
```

Then change `CachingIndustryStatisticsProvider`'s two `static readonly TimeSpan` fields into a
constructor parameter `StatbankCacheOptions options`, defaulted to `new()` for existing callers, and
use `options.PositiveDuration` / `options.NegativeDuration` where the constants were. The
cache-stampede gate, the `Size = 1` accounting and the "only cache a definitive answer" rule stay
exactly as they are.

### 13.3 The L2 decorator

```powershell
cd solution_microservices
dotnet new classlib -o src/Backend/FirmaData.Caching.Redis
Remove-Item src/Backend/FirmaData.Caching.Redis/Class1.cs
dotnet sln FirmaData.Microservices.sln add src/Backend/FirmaData.Caching.Redis/FirmaData.Caching.Redis.csproj
```

Add to `Directory.Packages.props`:

```xml
    <PackageVersion Include="Microsoft.Extensions.Caching.StackExchangeRedis" Version="8.0.10" />
```

`src/Backend/FirmaData.Caching.Redis/DistributedIndustryStatisticsProvider.cs`:

```csharp
using System.Text.Json;
using FirmaData.Application;
using FirmaData.Domain;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace FirmaData.Caching.Redis;

// L2: shared across every replica of the statistics service, and across restarts. Same decorator
// shape as CachingIndustryStatisticsProvider -- the inner provider cannot tell it is being cached,
// which is what lets the two stack without either knowing about the other.
public sealed class DistributedIndustryStatisticsProvider(
    IIndustryStatisticsProvider inner,
    IDistributedCache cache,
    ILogger<DistributedIndustryStatisticsProvider> logger) : IIndustryStatisticsProvider
{
    private const string CacheName = "statbank-redis";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DistributedCacheEntryOptions PositiveEntry = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) };
    private static readonly DistributedCacheEntryOptions NegativeEntry = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) };

    public async Task<Result<IndustryStatistics>> GetAsync(IndustryCode code, StatisticsYear year, CancellationToken ct)
    {
        var key = CacheKey(code, year);

        // A cache is an optimisation, never a dependency. Every Redis call is wrapped: if Redis is
        // unreachable the request still succeeds against the real source, one log line poorer.
        // Getting this wrong turns an optional cache into a new single point of failure -- the most
        // common way a "performance improvement" causes an outage.
        var cached = await TryGetAsync(key, ct);
        if (cached is not null)
        {
            CacheMetrics.RecordHit(CacheName);
            return cached.ToResult(code, year);
        }

        CacheMetrics.RecordMiss(CacheName);
        var result = await inner.GetAsync(code, year, ct);

        if (result.IsSuccess || result.Error.Type == ResultErrorType.NotFound)
        {
            await TrySetAsync(key, CachedStatistics.From(result), result.IsSuccess ? PositiveEntry : NegativeEntry, ct);
        }

        return result;
    }

    // Year discovery is cheap, changes once a year, and is already cached in-process by
    // StatbankClient. Adding a network hop for it would cost more than it saves.
    public Task<Result<IReadOnlyList<int>>> GetAvailableYearsAsync(CancellationToken ct) =>
        inner.GetAvailableYearsAsync(ct);

    public async Task InvalidateAsync(IndustryCode code, StatisticsYear year, CancellationToken ct)
    {
        try
        {
            await cache.RemoveAsync(CacheKey(code, year), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to invalidate {CacheKey} in Redis", CacheKey(code, year));
        }
    }

    private static string CacheKey(IndustryCode code, StatisticsYear year) => $"statbank:{code.Value}:{year.Value}";

    private async Task<CachedStatistics?> TryGetAsync(string key, CancellationToken ct)
    {
        try
        {
            var bytes = await cache.GetAsync(key, ct);
            return bytes is null ? null : JsonSerializer.Deserialize<CachedStatistics>(bytes, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis read failed for {CacheKey}; falling through to the source", key);
            return null;
        }
    }

    private async Task TrySetAsync(string key, CachedStatistics value, DistributedCacheEntryOptions options, CancellationToken ct)
    {
        try
        {
            await cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), options, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis write failed for {CacheKey}", key);
        }
    }

    // Domain types are not serialised directly: IndustryStatistics holds value objects with private
    // constructors, and a cache entry that cannot be deserialised after a refactor is a silent,
    // permanent cache miss. A flat DTO makes the wire format explicit and versionable.
    private sealed record CachedStatistics(bool Found, long? Workplaces, long? Employees, long? FullTimeEquivalents, decimal? WageSumMillionDkk)
    {
        public static CachedStatistics From(Result<IndustryStatistics> result) => result.IsSuccess
            ? new CachedStatistics(true, result.Value.Workplaces, result.Value.Employees, result.Value.FullTimeEquivalents, result.Value.WageSumMillionDkk)
            : new CachedStatistics(false, null, null, null, null);

        public Result<IndustryStatistics> ToResult(IndustryCode code, StatisticsYear year) => Found
            ? new IndustryStatistics(code, year, Workplaces, Employees, FullTimeEquivalents, WageSumMillionDkk)
            : Result.NotFound($"No industry statistics available for {year} (industry {code}).");
    }
}
```

`CacheMetrics` is `internal` in `FirmaData.Statbank`; either make it `public` or add an equivalent
`CacheMetrics` to this project emitting the same `firmadata.cache.hits`/`.misses` instrument names
with a different `cache` tag value. The second option keeps the library boundaries clean and lets the
dashboard show L1 and L2 hit ratios separately, which is what you want when tuning TTLs.

### 13.4 Registration

`src/Backend/FirmaData.Caching.Redis/ServiceCollectionExtensions.cs`:

```csharp
using FirmaData.Application;
using FirmaData.Statbank;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FirmaData.Caching.Redis;

public static class ServiceCollectionExtensions
{
    // Rebuilds the whole decorator chain rather than wrapping the existing registration: order
    // matters, and L1 must be OUTSIDE L2. Wrapping the existing IIndustryStatisticsProvider would
    // put Redis in front of the in-process cache and turn every hit into a network round trip.
    public static IServiceCollection AddRedisStatisticsCache(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = configuration.GetConnectionString("Redis");
            options.InstanceName = "firmadata:";
        });

        services.RemoveAll<IIndustryStatisticsProvider>();

        services.AddTransient<IIndustryStatisticsProvider>(provider => new CachingIndustryStatisticsProvider(
            new DistributedIndustryStatisticsProvider(
                provider.GetRequiredService<StatbankClient>(),
                provider.GetRequiredService<IDistributedCache>(),
                provider.GetRequiredService<ILogger<DistributedIndustryStatisticsProvider>>()),
            provider.GetRequiredService<IMemoryCache>(),
            new StatbankCacheOptions
            {
                // L1 is a burst absorber, not the source of truth: short enough that an
                // invalidation propagates within a minute, long enough to collapse a page's worth
                // of concurrent lookups into one Redis call.
                PositiveDuration = TimeSpan.FromSeconds(60),
                NegativeDuration = TimeSpan.FromSeconds(30),
            }));

        return services;
    }
}
```

In `FirmaData.Statbank.Api/Program.cs`, after `AddStatbankClient(...)`:

```csharp
        builder.Services.AddRedisStatisticsCache(builder.Configuration);

        builder.Services.AddHealthChecks().AddUpstream(/* ... existing statbank check ... */);
```

Redis is deliberately **not** added as a readiness check. It is optional by construction (§13.3), and
a health check that reports unhealthy on an optional dependency causes exactly the outage the
fallback was written to prevent.

### 13.5 Compose

```yaml
  redis:
    image: redis:7.4-alpine
    ports:
      - "16379:6379"
    command:
      # maxmemory + LRU: a cache that grows unbounded is a memory leak with a nicer name. The
      # statistics working set is small (a few thousand industry/year pairs), so 256mb is generous.
      - "redis-server"
      - "--maxmemory"
      - "256mb"
      - "--maxmemory-policy"
      - "allkeys-lru"
      - "--save"
      - ""
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 10s
      timeout: 3s
      retries: 5
```

`--save ""` disables RDB snapshots: this data is reconstructible from api.statbank.dk, so persisting
it buys nothing and costs disk I/O.

Add to `firmadata-statbank`:

```yaml
    environment:
      ConnectionStrings__Redis: redis:6379
    depends_on:
      redis:
        condition: service_healthy
```

### 13.6 Verify

```powershell
docker compose -f docker-compose.microservices.yml up --build -d

curl.exe -s "http://localhost:18080/api/v1/companies/16500836?year=2022" | Out-Null
docker compose -f docker-compose.microservices.yml exec redis redis-cli KEYS 'firmadata:*'

# The point of L2: restart the owner, the cache is still warm
docker compose -f docker-compose.microservices.yml restart firmadata-statbank
curl.exe -s "http://localhost:18080/api/v1/companies/16500836?year=2022" | Out-Null
curl.exe -s http://localhost:18082/metrics | Select-String firmadata_cache_hits_total

# The failure mode that matters: Redis down must not break anything
docker compose -f docker-compose.microservices.yml stop redis
curl.exe -s "http://localhost:18080/api/v1/companies/16500836?year=2022" | jq -e '.company.cvrNumber'
docker compose -f docker-compose.microservices.yml start redis
```

**Exit criteria**

- [ ] Keys appear in Redis under `firmadata:statbank:*`.
- [ ] A restart of the statistics service keeps the cache warm.
- [ ] With Redis stopped, requests still succeed (slower, with a warning log).
- [ ] L1 and L2 hit ratios are separately visible on `/metrics`.

---

## 14. Phase 14 — Horizontal scaling

**Goal:** run more than one replica of each stateless service and prove that nothing broke.

**Why now:** Redis removed the last piece of per-replica state that mattered. This phase is the
first time the split delivers the benefit it was chosen for.

### 14.1 Remove fixed host ports

A published host port cannot be shared by three replicas. Replicated services lose their `ports:`
block; the gateway and the frontend keep theirs.

```yaml
  firmadata-cvr:
    # ports: removed -- reachable only inside the network, and now scalable
    deploy:
      replicas: 2
```

Compose honours `deploy.replicas` in `docker compose up` (v2). Debugging a specific replica is done
with `docker compose exec --index=2 firmadata-cvr sh`.

### 14.2 Load balancing

Compose's embedded DNS returns all replica IPs for a service name, and .NET's `SocketsHttpHandler`
caches connections per resolved endpoint — so without configuration, one enrichment replica can pin
itself to one CVR replica for the lifetime of its connections. Two fixes, both needed:

```csharp
        // In ServiceDefaults' AddInternalResiliencePipeline, on the primary handler:
        services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() =>
            new SocketsHttpHandler
            {
                // Forces periodic re-resolution of the service name, so a new replica gets traffic
                // without restarting the caller, and connections do not pin to one destination.
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            }));
```

For traffic through the gateway, YARP balances explicitly:

```json
      "enrichment": {
        "LoadBalancingPolicy": "PowerOfTwoChoices",
        "Destinations": {
          "primary": { "Address": "http://firmadata-enrichment:8080/" }
        }
      }
```

> `PowerOfTwoChoices` (pick two at random, send to the less loaded) beats round-robin whenever
> request cost varies — which it does here: a single-CVR lookup is one upstream call, a name search
> is up to thirty. With one DNS name resolving to N replicas, YARP still needs
> `"Destinations"` per replica for true per-destination balancing; with Compose DNS round-robin as
> the only discovery mechanism, accept DNS-level distribution and revisit when moving to an
> orchestrator with real service discovery.

### 14.3 Statelessness audit

Everything in the list below must be false for every replicated service. Check each explicitly:

| Question | FirmaData answer |
| --- | --- |
| Does the service keep per-user state in memory? | No — no sessions, no auth state yet |
| Does it write to local disk? | No — logs go to stdout |
| Does it hold a cache that must be consistent across replicas? | Statistics: Redis (§13). Year discovery: in-process, 24 h, identical everywhere — acceptable divergence |
| Does it hold a circuit-breaker state that should be shared? | Yes, per replica — **accepted**: each replica learns independently, which is the correct behaviour for a per-connection failure |
| Does it schedule background work that must run once? | Not yet — Phase 15 introduces one, and handles it there |

### 14.4 Verify

```powershell
docker compose -f docker-compose.microservices.yml up --build -d --scale firmadata-cvr=2 --scale firmadata-enrichment=2
docker compose -f docker-compose.microservices.yml ps

1..40 | ForEach-Object { curl.exe -s "http://localhost:18080/api/v1/companies/16500836?year=2022" | Out-Null }
docker compose -f docker-compose.microservices.yml logs firmadata-cvr | Select-String "GET /api/v1/companies" | Measure-Object
```

Both replicas should show traffic. Then kill one mid-load and confirm no user-visible errors:

```powershell
docker compose -f docker-compose.microservices.yml kill --index=1 firmadata-cvr
```

**Exit criteria**

- [ ] Two replicas of CVR and enrichment both receive traffic.
- [ ] Killing one replica produces no failed user requests (retries and the healthcheck cover it).
- [ ] Cache hit ratio does not drop when scaling out — the Redis L2 is doing its job.

---

## 15. Phase 15 — Asynchronous messaging

**Goal:** work that must not happen on the request path — cache pre-warming and invalidation — moves
to a broker, and the statistics service stops being the only thing that can warm its own cache.

**Why:** the first request for a given industry/year after a deploy pays the full Statbank latency.
With a worker pre-warming the top industries nightly, that cost moves off the user's request
entirely. It also introduces the one messaging pattern this domain genuinely needs, rather than
adding a broker for its own sake.

**What this is not:** an excuse to make the read path asynchronous. Company lookup stays synchronous
HTTP. Eventual consistency in a lookup API would be a downgrade, not an upgrade.

### 15.1 Broker

```yaml
  rabbitmq:
    image: rabbitmq:4.0-management-alpine
    ports:
      - "15680:5672"
      - "15681:15672"
    environment:
      RABBITMQ_DEFAULT_USER: firmadata
      RABBITMQ_DEFAULT_PASS: firmadata
    healthcheck:
      test: ["CMD", "rabbitmq-diagnostics", "-q", "ping"]
      interval: 15s
      timeout: 10s
      retries: 5
```

Credentials in plain text here are replaced in Phase 19. The management UI is on
`http://localhost:15681` (`firmadata`/`firmadata`).

### 15.2 Contracts

```powershell
dotnet new classlib -o src/Backend/FirmaData.Messaging.Contracts
```

```csharp
namespace FirmaData.Messaging.Contracts;

// Events, not commands: the publisher states what happened and does not care who reacts. Adding a
// second consumer later requires no change to the publisher.

// Published when the set of years Statbank publishes changes -- everyone caching a "latest year"
// needs to know.
public sealed record StatisticsYearsChanged(IReadOnlyList<int> Years, DateTimeOffset ObservedAt);

// Published when a specific industry/year pair must be re-read from the source.
public sealed record IndustryStatisticsInvalidated(string IndustryCode, int Year, string Reason, DateTimeOffset ObservedAt);

// Published by the worker before it pre-warms, so the cache fill is observable.
public sealed record StatisticsWarmupRequested(IReadOnlyList<string> IndustryCodes, int Year, DateTimeOffset RequestedAt);
```

### 15.3 Packages and wiring

`Directory.Packages.props`:

```xml
    <PackageVersion Include="MassTransit" Version="8.3.0" />
    <PackageVersion Include="MassTransit.RabbitMQ" Version="8.3.0" />
```

Shared registration in `FirmaData.ServiceDefaults`:

```csharp
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FirmaData.ServiceDefaults;

public static class MessagingExtensions
{
    public static IServiceCollection AddFirmaDataMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configureConsumers = null)
    {
        services.AddMassTransit(bus =>
        {
            configureConsumers?.Invoke(bus);

            // Kebab-case queue names, prefixed per service, so a queue in the management UI names
            // the consumer that owns it without a lookup table.
            bus.SetKebabCaseEndpointNameFormatter();

            bus.UsingRabbitMq((context, rabbit) =>
            {
                rabbit.Host(configuration.GetConnectionString("RabbitMq"));

                // Retry, then redeliver with a longer delay, then dead-letter. Without the final
                // step a poison message loops forever and the queue never drains.
                rabbit.UseMessageRetry(retry => retry.Exponential(
                    retryLimit: 3,
                    minInterval: TimeSpan.FromSeconds(1),
                    maxInterval: TimeSpan.FromSeconds(30),
                    intervalDelta: TimeSpan.FromSeconds(2)));
                rabbit.UseDelayedRedelivery(redelivery => redelivery.Intervals(
                    TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)));

                rabbit.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}
```

### 15.4 The consumer

In `FirmaData.Statbank.Api`, a consumer that evicts both cache levels:

```csharp
using FirmaData.Caching.Redis;
using FirmaData.Domain;
using FirmaData.Messaging.Contracts;
using MassTransit;

namespace FirmaData.Statbank.Api.Consumers;

public sealed class IndustryStatisticsInvalidatedConsumer(
    DistributedIndustryStatisticsProvider redisCache,
    ILogger<IndustryStatisticsInvalidatedConsumer> logger) : IConsumer<IndustryStatisticsInvalidated>
{
    public async Task Consume(ConsumeContext<IndustryStatisticsInvalidated> context)
    {
        var code = IndustryCode.TryCreate(context.Message.IndustryCode);
        var year = StatisticsYear.TryCreate(context.Message.Year);

        // A malformed message is dropped, not retried: no number of redeliveries will make "abc"
        // a valid industry code, and retrying it only delays the rest of the queue.
        if (code.IsFailure || year.IsFailure)
        {
            logger.LogWarning("Discarding invalidation for {IndustryCode}/{Year}: {Error}",
                context.Message.IndustryCode, context.Message.Year, code.IsFailure ? code.Error.Message : year.Error.Message);
            return;
        }

        await redisCache.InvalidateAsync(code.Value, year.Value, context.CancellationToken);

        // L1 is not evicted directly -- each replica's 60s TTL (§13.4) expires it. Evicting L1
        // across replicas would need a fan-out exchange per replica, which is a lot of machinery
        // to shave 60 seconds off an annual statistic.
        logger.LogInformation("Invalidated {IndustryCode}/{Year} ({Reason})",
            code.Value, year.Value, context.Message.Reason);
    }
}
```

Registration in that service's `Program.cs`:

```csharp
        builder.Services.AddFirmaDataMessaging(builder.Configuration,
            bus => bus.AddConsumer<IndustryStatisticsInvalidatedConsumer>());
```

Note `DistributedIndustryStatisticsProvider` must be registered as itself as well as inside the
decorator chain for the consumer to resolve it — add
`services.AddSingleton<DistributedIndustryStatisticsProvider>()` in §13.4's extension and have the
chain resolve that instance rather than constructing a second one.

### 15.5 The worker

```powershell
dotnet new worker --use-program-main -o src/Backend/FirmaData.Statistics.Worker
```

```csharp
using FirmaData.Messaging.Contracts;
using MassTransit;

namespace FirmaData.Statistics.Worker;

// Pre-warms the cache for the industries that actually get looked up, so the first user of the day
// does not pay Statbank's cold latency. Runs on a schedule, not on the request path.
public sealed class CacheWarmupService(IPublishEndpoint publisher, IConfiguration configuration, ILogger<CacheWarmupService> logger)
    : BackgroundService
{
    // In a single-replica worker this is enough. If the worker is ever scaled beyond one replica,
    // this needs a distributed lock (Redis SET NX with a TTL) or a scheduler that guarantees
    // single execution -- otherwise N replicas warm the same keys N times and multiply the load on
    // the source this phase exists to protect.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = configuration.GetValue("Warmup:Interval", TimeSpan.FromHours(6));
        var codes = configuration.GetSection("Warmup:IndustryCodes").Get<string[]>() ?? [];
        var year = configuration.GetValue("Warmup:Year", DateTime.UtcNow.Year - 2);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (codes.Length > 0)
            {
                logger.LogInformation("Requesting warmup of {Count} industry codes for {Year}", codes.Length, year);
                await publisher.Publish(new StatisticsWarmupRequested(codes, year, DateTimeOffset.UtcNow), stoppingToken);
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
```

The corresponding `StatisticsWarmupRequestedConsumer` in the statistics service simply calls
`IIndustryStatisticsProvider.GetAsync` for each code — which populates both cache levels through the
existing decorator chain, with no cache-specific code at all. That is the decorator pattern paying
off a third time.

Rate-limit the warmup loop: `codes` of length 200 fired at once is a self-inflicted denial of
service against api.statbank.dk. Consume them with MassTransit's concurrency limit set to 2:

```csharp
            bus.UsingRabbitMq((context, rabbit) =>
            {
                rabbit.ReceiveEndpoint("statistics-warmup", endpoint =>
                {
                    endpoint.PrefetchCount = 2;
                    endpoint.ConcurrentMessageLimit = 2;
                    endpoint.ConfigureConsumer<StatisticsWarmupRequestedConsumer>(context);
                });
            });
```

### 15.6 Verify

```powershell
docker compose -f docker-compose.microservices.yml up --build -d
Start-Process http://localhost:15681   # queues, one per consumer, kebab-cased

# Invalidate and observe the next request miss the cache
docker compose -f docker-compose.microservices.yml exec firmadata-statistics-worker `
  dotnet FirmaData.Statistics.Worker.dll --publish-invalidation 651200 2022
```

(Or publish from the RabbitMQ management UI directly onto the `industry-statistics-invalidated`
exchange.)

**Exit criteria**

- [ ] Queues visible in the management UI, one per consumer.
- [ ] An invalidation message removes the Redis key.
- [ ] A poison message lands in `_error` after its retries, and the queue keeps draining.
- [ ] Stopping RabbitMQ does not affect the synchronous lookup path at all.

---

## 16. Phase 16 — Distributed tracing

**Goal:** one request, one waterfall, five services. The correlation id tells you *which* logs
belong together; a trace tells you *where the time went*.

**Why now:** with five services, two cache levels and a broker, "the API is slow" is no longer a
diagnosable statement without traces. This is the highest-value observability upgrade in this
document.

### 16.1 Tempo

```yaml
  tempo:
    image: grafana/tempo:2.6.0
    command: ["-config.file=/etc/tempo/tempo.yml"]
    ports:
      - "13200:3200"   # Tempo API, queried by Grafana
      - "14317:4317"   # OTLP gRPC, written to by the services
    volumes:
      - ./ops/tempo/tempo.yml:/etc/tempo/tempo.yml:ro
```

`ops/tempo/tempo.yml`:

```yaml
server:
  http_listen_port: 3200

distributor:
  receivers:
    otlp:
      protocols:
        grpc:
          endpoint: 0.0.0.0:4317

storage:
  trace:
    backend: local
    local:
      path: /var/tempo/traces
    wal:
      path: /var/tempo/wal

# Local disk, no retention policy: adequate for a single-host demo, and explicitly not a production
# trace store. A real deployment points `backend` at object storage (S3/GCS/Azure Blob) with a
# retention period, because traces are the fastest-growing telemetry you will produce.
compactor:
  compaction:
    block_retention: 24h
```

`ops/grafana/provisioning/datasources/datasource.yml` — add Tempo alongside Prometheus:

```yaml
  - name: Tempo
    uid: tempo
    type: tempo
    access: proxy
    url: http://tempo:3200
    editable: false
    jsonData:
      tracesToLogsV2:
        datasourceUid: loki           # wired up in Phase 17
        filterByTraceID: true
      tracesToMetrics:
        datasourceUid: prometheus
```

### 16.2 Instrumentation

`Directory.Packages.props` (or `FirmaData.ServiceDefaults.csproj`, which is outside CPM):

```xml
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.17.0" />
```

In `ServiceDefaultsExtensions.AddServiceDefaults`, after the metrics block:

```csharp
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing
            .ConfigureResource(resource => resource.AddService(serviceName))
            .AddAspNetCoreInstrumentation(options =>
            {
                // Health and metrics endpoints are polled every few seconds by Prometheus and the
                // orchestrator. Tracing them would drown every real request in noise and dominate
                // storage -- easily 95% of all spans in a low-traffic service.
                options.Filter = context =>
                    !context.Request.Path.StartsWithSegments("/health") &&
                    !context.Request.Path.StartsWithSegments("/metrics");
                options.RecordException = true;
            })
            .AddHttpClientInstrumentation(options => options.RecordException = true)
            .AddSource("MassTransit")
            .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(
                builder.Configuration.GetValue("Tracing:SampleRatio", 1.0))))
            .AddOtlpExporter(options =>
                options.Endpoint = new Uri(builder.Configuration["Tracing:OtlpEndpoint"] ?? "http://tempo:4317")));
```

> **Sampling.** 100% is right for a demo and wrong above a few hundred requests per second.
> `ParentBasedSampler` is the important part: it makes the *gateway's* decision authoritative for
> the whole trace, so you never get a trace with the middle three hops missing. Set
> `Tracing:SampleRatio` to something like 0.1 in production and keep it at 1.0 for error traces via
> a tail-sampling collector when that becomes necessary.

### 16.3 Correlation id and trace id together

Both are kept, on purpose:

| | Correlation id | Trace id |
| --- | --- | --- |
| Set by | `CorrelationIdMiddleware` (or a caller) | W3C `traceparent`, by the OTel SDK |
| Visible to | The end user, on the error page | Operators, in Grafana |
| Survives | Everything, including a client-supplied value | Only within the sampled trace |
| Use | "Customer says request X failed" | "Where did the 3 seconds go" |

Bind them so either one finds the other — in `AddServiceDefaults`'s Serilog configuration:

```csharp
            .Enrich.WithProperty("service", serviceName)
            .Enrich.With<TraceIdEnricher>()
```

```csharp
using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace FirmaData.ServiceDefaults;

// Puts the active trace/span id on every log line, which is what makes Grafana's "logs for this
// trace" link work in both directions.
public sealed class TraceIdEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        logEvent.AddPropertyIfAbsent(factory.CreateProperty("traceId", activity.TraceId.ToString()));
        logEvent.AddPropertyIfAbsent(factory.CreateProperty("spanId", activity.SpanId.ToString()));
    }
}
```

And add the correlation id as a span tag, so a trace can be found from a user-reported id — in
`CorrelationIdMiddleware.InvokeAsync`:

```csharp
        Activity.Current?.SetTag("firmadata.correlation_id", correlationId);
```

### 16.4 Verify

```powershell
curl.exe -s "http://localhost:18080/api/v1/companies/16500836?year=2022" | Out-Null
Start-Process "http://localhost:13000/explore"   # Grafana → Explore → Tempo → Search
```

The waterfall for one lookup should show: `firmadata-gateway` → `firmadata-enrichment` →
`firmadata-cvr` → `apicvr.dk`, then `firmadata-statbank` → `api.statbank.dk`, with the
sequential dependency between the CVR call and the statistics call visible as the reason the two
cannot be parallelised.

**Exit criteria**

- [ ] A single request produces one trace spanning at least four services.
- [ ] Health and metrics scrapes produce no traces.
- [ ] A log line in Loki links to its trace and back (after Phase 17).
- [ ] The trace shows the CVR → Statbank sequencing that `CompanyEnrichmentService` documents.

---

## 17. Phase 17 — Centralised logging

**Goal:** one query surface for five services' structured logs, correlated with traces.

`docker compose logs | Select-String <id>` works for a demo and fails the moment services are
replicated or a container is recycled.

```yaml
  loki:
    image: grafana/loki:3.2.0
    command: ["-config.file=/etc/loki/local-config.yaml"]
    ports:
      - "13100:3100"
```

`Directory.Packages.props`:

```xml
    <PackageVersion Include="Serilog.Sinks.Grafana.Loki" Version="8.3.0" />
```

In `AddServiceDefaults`'s Serilog configuration, add the sink alongside the console one — never
instead of it, because container stdout stays the last-resort diagnostic when Loki itself is the
thing that is broken:

```csharp
            .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
            .WriteTo.GrafanaLoki(
                builder.Configuration["Logging:LokiUrl"] ?? "http://loki:3100",
                labels: [new LokiLabel { Key = "service", Value = serviceName }],
                // Only low-cardinality fields become Loki labels. Putting correlationId or traceId
                // in labels would create a new stream per request and destroy Loki's index -- they
                // belong in the log line's structured payload, which Loki can still filter on.
                propertiesAsLabels: ["level"])
```

Grafana datasource:

```yaml
  - name: Loki
    uid: loki
    type: loki
    access: proxy
    url: http://loki:3100
    editable: false
    jsonData:
      derivedFields:
        - name: TraceID
          matcherRegex: "\"traceId\":\"(\\w+)\""
          url: "$${__value.raw}"
          datasourceUid: tempo
```

Useful queries once it is up:

```logql
{service=~"firmadata-.+"} | json | correlationId = "abc123"
{service="firmadata-enrichment"} | json | level = "Error"
sum by (service) (rate({service=~"firmadata-.+"} | json | level = "Error" [5m]))
```

**Exit criteria**

- [ ] All five services' logs queryable in Grafana → Explore → Loki.
- [ ] A correlation id filters to the request's lines across every service.
- [ ] A log line links to its trace; a trace links back to its logs.

---

## 18. Phase 18 — Authentication and authorisation

**Goal:** the API stops being anonymous. Machine clients authenticate with OAuth2 client
credentials; the frontend authenticates users with OIDC; the gateway is the only place that
validates anything.

The README's trade-off table names this as the first thing production would need.

### 18.1 Identity provider

```yaml
  keycloak:
    image: quay.io/keycloak/keycloak:26.0
    command: ["start-dev", "--import-realm"]
    ports:
      - "18180:8080"
    environment:
      KC_BOOTSTRAP_ADMIN_USERNAME: admin
      KC_BOOTSTRAP_ADMIN_PASSWORD: admin
      KC_HEALTH_ENABLED: "true"
    volumes:
      - ./ops/keycloak/firmadata-realm.json:/opt/keycloak/data/import/firmadata-realm.json:ro
    healthcheck:
      test: ["CMD-SHELL", "exec 3<>/dev/tcp/127.0.0.1/9000 && echo -e 'GET /health/ready HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && cat <&3 | grep -q '\"status\": \"UP\"'"]
      interval: 15s
      timeout: 5s
      retries: 10
```

`start-dev` is a development mode with an in-memory database and no TLS — correct for this guide,
never for production, where Keycloak runs `start` against PostgreSQL behind TLS.

`ops/keycloak/firmadata-realm.json` defines, as code:

* realm `firmadata`
* client `firmadata-api` — confidential, service accounts enabled (client credentials), scope
  `companies:read`
* client `firmadata-web` — confidential, standard flow (authorisation code + PKCE), redirect URI
  `http://localhost:18090/signin-oidc`
* roles `reader` and `admin`, and a test user

Importing the realm as a file rather than clicking through the admin console is what makes the auth
setup reproducible — and it is the difference between a demo that survives a `docker compose down -v`
and one that does not.

### 18.2 Validation at the gateway

`FirmaData.Gateway.csproj`:

```xml
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
```

`Program.cs`:

```csharp
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = builder.Configuration["Auth:Authority"];
                options.Audience = builder.Configuration["Auth:Audience"];
                // Development only: Keycloak's start-dev mode serves HTTP.
                options.RequireHttpsMetadata = builder.Environment.IsProduction();
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    // Default is 5 minutes, which means an expired token is accepted for five more
                    // minutes -- rarely what anyone intends.
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("companies-read", policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim("scope", "companies:read"));
        });
```

```csharp
        app.UseAuthentication();
        app.UseAuthorization();
```

Per route, in `appsettings.json`:

```json
      "companies-by-cvr": {
        "ClusterId": "enrichment",
        "AuthorizationPolicy": "companies-read",
        "RateLimiterPolicy": "per-client",
        "Match": { "Path": "/api/v1/companies/{**catch-all}" }
      }
```

`/health/*` and `/metrics` stay anonymous — an orchestrator cannot present a token, and a probe that
requires auth fails in exactly the situation you need it most.

### 18.3 The services behind the gateway

Three options, in increasing order of rigour:

| Option | What it means | When |
| --- | --- | --- |
| **Network trust** | Services accept anything from the compose network; only the gateway validates | This guide's choice, adequate on a private network with no other tenants |
| **Token pass-through** | The gateway forwards the JWT; each service validates it independently | Right as soon as more than one caller can reach a service |
| **mTLS + service identity** | Every hop authenticated cryptographically; typically a service mesh | Beyond a compose-based deployment; named here and not implemented |

If you take option 2, add the same `AddJwtBearer` block to each service, and forward the header with
a `DelegatingHandler` in the same slot as `CorrelationIdForwardingHandler`.

### 18.4 The frontend

`FirmaData.Web` switches from anonymous to OIDC:

```csharp
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie()
            .AddOpenIdConnect(options =>
            {
                options.Authority = builder.Configuration["Auth:Authority"];
                options.ClientId = "firmadata-web";
                options.ClientSecret = builder.Configuration["Auth:ClientSecret"];
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = true;      // so the API client can attach the access token
                options.Scope.Add("companies:read");
            });
```

A handler then attaches the saved access token to every call to the gateway:

```csharp
public sealed class AccessTokenHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await (accessor.HttpContext?.GetTokenAsync("access_token") ?? Task.FromResult<string?>(null));
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, ct);
    }
}
```

**Note the statelessness regression:** cookie authentication puts session state back into the
frontend. With replicas, that needs a shared data-protection key ring
(`AddDataProtection().PersistKeysToStackExchangeRedis(...)`, reusing the Redis from Phase 13) or
every replica issues cookies the others cannot read. This is the exact trap Phase 14's audit was
written to catch — re-run that audit after this phase.

### 18.5 Verify

```powershell
# No token -> 401
curl.exe -s -o NUL -w "%{http_code}`n" "http://localhost:18080/api/v1/companies/16500836"

# Client credentials
$token = (curl.exe -s -X POST "http://localhost:18180/realms/firmadata/protocol/openid-connect/token" `
  -d "grant_type=client_credentials" -d "client_id=firmadata-api" -d "client_secret=<secret>" | jq -r .access_token)

curl.exe -s -H "Authorization: Bearer $token" "http://localhost:18080/api/v1/companies/16500836?year=2022" | jq .
```

**Exit criteria**

- [ ] Anonymous request → 401; valid token → 200; token without the scope → 403.
- [ ] `/health/live`, `/health/ready`, `/metrics` still anonymous.
- [ ] The frontend redirects to Keycloak, logs in, and shows results.
- [ ] Token expiry produces a clean re-authentication, not a 500.

---

## 19. Phase 19 — Secrets management

**Goal:** no credential appears in a compose file, an `appsettings.json`, or the shell history.

After Phase 18 the stack holds a Keycloak client secret, RabbitMQ credentials, a Postgres password
(Phase 21) and a Redis connection string. Right now they are all plaintext.

### 19.1 Step one — Docker secrets and key-per-file

The smallest change with real value. .NET reads a directory of files as configuration natively, and
`/run/secrets` is exactly such a directory:

```csharp
        // Each file in /run/secrets becomes a configuration key; "__" in the filename is the
        // section separator, so a file named Auth__ClientSecret binds to Auth:ClientSecret with no
        // code change anywhere else. Added last, so it wins over appsettings and environment.
        builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);
```

```yaml
secrets:
  auth_client_secret:
    file: ./ops/secrets/Auth__ClientSecret
  rabbitmq_connection:
    file: ./ops/secrets/ConnectionStrings__RabbitMq

services:
  firmadata-gateway:
    secrets:
      - source: auth_client_secret
        target: Auth__ClientSecret
```

`ops/secrets/` goes in `.gitignore`, with an `ops/secrets/README.md` listing the required files and
their formats — so a new developer knows what to create without any of it being committed.

This removes secrets from compose files and images. It does **not** give you rotation, audit, or
per-service access control.

### 19.2 Step two — Vault

```yaml
  vault:
    image: hashicorp/vault:1.18
    ports:
      - "18200:8200"
    cap_add: ["IPC_LOCK"]
    environment:
      VAULT_DEV_ROOT_TOKEN_ID: firmadata-dev-token
      VAULT_DEV_LISTEN_ADDRESS: "0.0.0.0:8200"
```

Dev mode is in-memory and auto-unsealed. Production Vault needs persistent storage, a real unseal
strategy (auto-unseal via a cloud KMS), and TLS — say so out loud rather than shipping this
configuration and hoping.

Access pattern per service, in order of preference:

1. **AppRole** — each service has a role id (config) and a secret id (injected at start), exchanges
   them for a short-lived token, and reads only its own path.
2. **Vault Agent sidecar** — the agent authenticates and templates secrets to a file the app reads
   via `AddKeyPerFile`, so the application never speaks to Vault at all. This is the pattern that
   composes best with §19.1: adopting Vault becomes a deployment change, not a code change.
3. **Direct SDK calls** (`VaultSharp` + a custom `IConfigurationSource`) — the most code and the most
   coupling; use only when dynamic secrets (per-request database credentials) are actually needed.

Take option 2. The application keeps `AddKeyPerFile` from §19.1 and does not know Vault exists.

### 19.3 Rotation

A secret that cannot be rotated without downtime is not managed, only stored. Verify all three:

| Secret | Rotation | Restart needed |
| --- | --- | --- |
| Keycloak client secret | New secret in Keycloak, update Vault, agent re-templates | Yes, unless `IOptionsMonitor` + `reloadOnChange` is wired through |
| RabbitMQ credentials | New user, update, remove old | Yes |
| Postgres password (§21) | `ALTER ROLE`, update, reconnect | Connection pool drains naturally |

**Exit criteria**

- [ ] `git grep -iE "password|secret|clientsecret" -- '*.yml' '*.json'` returns nothing but key names.
- [ ] `docker compose config` shows no plaintext credential.
- [ ] A rotated secret is picked up by a documented procedure with a known downtime cost.

---

## 20. Phase 20 — Quotas and backpressure

**Goal:** one misbehaving client cannot degrade the service for everyone else, and overload
degrades predictably instead of collapsing.

Phase 6's rate limiter partitions by IP, which is wrong the moment clients share a NAT or a token.
With authentication in place, partition by identity.

```csharp
            options.AddPolicy("per-client", context =>
            {
                // Client identity first, user second, IP only as a last resort. Partitioning by IP
                // alone means one NAT'd office shares a bucket, and one authenticated client can
                // evade its own limit by changing address.
                var partitionKey =
                    context.User.FindFirst("client_id")?.Value ??
                    context.User.FindFirst("sub")?.Value ??
                    context.Connection.RemoteIpAddress?.ToString() ??
                    "anonymous";

                return RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ => new TokenBucketRateLimiterOptions
                {
                    // A token bucket, not a fixed window: it permits a burst (a page of 10 search
                    // results resolving concurrently) while still capping the sustained rate. A
                    // fixed window either blocks the legitimate burst or allows twice the intended
                    // rate across a window boundary.
                    TokenLimit = 120,
                    TokensPerPeriod = 60,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });

            // A second, global limiter: caps total concurrent in-flight proxied requests so the
            // gateway sheds load before the services behind it exhaust their thread pools.
            options.AddConcurrencyLimiter("gateway-concurrency", limiter =>
            {
                limiter.PermitLimit = 200;
                limiter.QueueLimit = 50;
                limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
```

Rejection must be informative — a 429 without `Retry-After` forces clients to guess, and they guess
aggressively:

```csharp
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
                }

                RateLimitMetrics.RecordRejection(context.HttpContext.User.FindFirst("client_id")?.Value ?? "anonymous");
                await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too Many Requests",
                    Detail = "Rate limit exceeded. Retry after the interval in the Retry-After header.",
                }, ct);
            };
```

Emit `firmadata.ratelimit.rejections{client}` and alert on it (Phase 24) — a client suddenly hitting
its limit is either a bug or a new integration, and both are worth knowing about within minutes.

**Exit criteria**

- [ ] Two clients with separate tokens have independent budgets.
- [ ] Exceeding the limit yields 429 + `Retry-After` + a `ProblemDetails` body.
- [ ] A burst of 100 concurrent requests is shed cleanly, not by timing out.
- [ ] Rejections are visible per client on the dashboard.

---

## 21. Phase 21 — Per-service persistence

**Goal:** the enrichment service records what was looked up, when, by whom, and what was returned —
making lookups auditable and replayable.

**Why this and not a database for CVR or statistics:** both upstream sources are authoritative and
own their data; caching them is right, owning a copy is not. What FirmaData genuinely owns is *its
own request history* — which is a legitimate, service-local dataset.

**The rule, stated once:** each service owns its schema, and no service reads another's tables. A
shared database is a distributed monolith with worse failure modes than the monolith it replaced.

```yaml
  postgres-enrichment:
    image: postgres:17-alpine
    ports:
      - "15432:5432"
    environment:
      POSTGRES_DB: firmadata_enrichment
      POSTGRES_USER: enrichment
      POSTGRES_PASSWORD_FILE: /run/secrets/postgres_enrichment_password
    secrets:
      - postgres_enrichment_password
    volumes:
      - enrichment-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U enrichment -d firmadata_enrichment"]
      interval: 10s
      timeout: 5s
      retries: 5

volumes:
  enrichment-data:
```

Packages:

```xml
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="8.0.10" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="8.0.10" />
```

The entity and context, in a new `FirmaData.Enrichment.Persistence` project so the host stays a
composition root:

```csharp
namespace FirmaData.Enrichment.Persistence;

// An audit record, not a cache. Deliberately denormalised and append-only: it answers "what did we
// tell this client at 14:03 last Tuesday", which requires storing what was actually returned, not
// a foreign key to something that may since have changed upstream.
public sealed class LookupAudit
{
    public long Id { get; set; }

    public required string CorrelationId { get; set; }

    public required string LookupType { get; set; }        // "cvr" | "name"

    public required string Query { get; set; }

    public int? Year { get; set; }

    public required string Outcome { get; set; }           // Ok | NotFound | Unavailable | ...

    public required string StatisticsStatus { get; set; }  // EnrichmentStatus

    public int ResultCount { get; set; }

    public int DurationMs { get; set; }

    public string? ClientId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
```

```csharp
using Microsoft.EntityFrameworkCore;

namespace FirmaData.Enrichment.Persistence;

public sealed class EnrichmentDbContext(DbContextOptions<EnrichmentDbContext> options) : DbContext(options)
{
    public DbSet<LookupAudit> LookupAudits => Set<LookupAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var audit = modelBuilder.Entity<LookupAudit>();
        audit.ToTable("lookup_audit");
        audit.HasKey(a => a.Id);
        audit.Property(a => a.CorrelationId).HasMaxLength(64).IsRequired();
        audit.Property(a => a.Query).HasMaxLength(256).IsRequired();
        audit.Property(a => a.LookupType).HasMaxLength(16).IsRequired();
        audit.Property(a => a.Outcome).HasMaxLength(32).IsRequired();
        audit.Property(a => a.StatisticsStatus).HasMaxLength(32).IsRequired();

        // The two queries this table exists to serve: "what happened around time T" and "trace this
        // correlation id". Both indexed up front, because adding an index to a large append-only
        // table later means a lock or a CONCURRENTLY dance.
        audit.HasIndex(a => a.OccurredAt);
        audit.HasIndex(a => a.CorrelationId);
    }
}
```

Writing must never fail a lookup. The audit is a side effect, not part of the transaction:

```csharp
// Enqueue to an in-memory channel, drained by a hosted service that batches inserts. If the
// database is unreachable, the channel drops oldest-first and logs -- a lookup never fails, and
// never waits, because of the audit log.
public sealed class AuditWriter(Channel<LookupAudit> channel, ILogger<AuditWriter> logger)
{
    public void Enqueue(LookupAudit audit)
    {
        if (!channel.Writer.TryWrite(audit))
        {
            logger.LogWarning("Audit queue full; dropping record for {CorrelationId}", audit.CorrelationId);
        }
    }
}
```

Migrations, and the deployment rule that goes with them:

```powershell
dotnet ef migrations add InitialCreate --project src/Backend/FirmaData.Enrichment.Persistence --startup-project src/Backend/FirmaData.Enrichment.Api
```

> **Never call `Database.Migrate()` at startup with replicas.** N replicas starting simultaneously
> means N concurrent migration attempts on the same schema. Run migrations as an explicit deployment
> step (`dotnet ef database update`, or a one-shot migration container that must exit 0 before the
> service starts), and make every migration backwards-compatible with the currently running version
> so a rollback does not require a schema rollback.

Add a readiness check for Postgres here — unlike Redis, an audit database that is down means an
obligation is not being met, and readiness should reflect that. If the audit is regulatory, make it
`Unhealthy`; if it is best-effort, `Degraded`. Decide explicitly and write down which.

**Exit criteria**

- [ ] Every lookup produces exactly one audit row.
- [ ] Stopping Postgres does not fail lookups; rows are dropped with a warning.
- [ ] Migrations run as a deployment step, not at startup.
- [ ] The volume survives `docker compose down` (and is removed only by `down -v`).

---

## 22. Phase 22 — Contract testing and versioning

**Goal:** a breaking change to an internal contract fails CI in the *provider's* pipeline, before
deployment — not at runtime in the consumer.

This is the cost the split imposed: in the monolith the compiler caught every contract break. Across
a process boundary, nothing does. Contract tests put that safety net back.

### 22.1 Pact

```xml
    <PackageVersion Include="PactNet" Version="5.0.0" />
```

**Consumer side** (`FirmaData.Enrichment.Api.IntegrationTests`) — describes what the enrichment
service actually needs from the CVR service, and produces a pact file:

```csharp
using PactNet;

namespace FirmaData.Enrichment.Api.IntegrationTests.Contracts;

public sealed class CvrServiceContractTests
{
    private readonly IPactBuilderV4 _pact = Pact.V4("FirmaData.Enrichment", "FirmaData.Cvr",
        new PactConfig { PactDir = "../../../../pacts" }).WithHttpInteractions();

    [Fact]
    public async Task GetByCvr_ReturnsTheFieldsTheOrchestratorNeeds()
    {
        _pact
            .UponReceiving("a lookup for a known CVR number")
                .Given("company 16500836 exists")
                .WithRequest(HttpMethod.Get, "/api/v1/companies/16500836")
            .WillRespond()
                .WithStatus(HttpStatusCode.OK)
                .WithJsonBody(new
                {
                    cvrNumber = "16500836",
                    name = "LB Forsikring A/S",
                    address = new { street = "Farvergade 17", postalCode = "1463", city = "København K" },
                    industryCode = "651200",
                    industryDescription = "Skadesforsikring",
                    employeeCount = 500,
                    status = "Active",
                });

        await _pact.VerifyAsync(async context =>
        {
            var client = new HttpClient { BaseAddress = context.MockServerUri };
            var directory = new HttpCompanyDirectory(client);

            var result = await directory.GetByCvrAsync(CvrNumber.TryCreate("16500836").Value, default);

            result.IsSuccess.Should().BeTrue();
            result.Value.IndustryCode.Value.Should().Be("651200");
        });
    }
}
```

**Provider side** (`FirmaData.Cvr.Api.IntegrationTests`) replays every interaction in that pact
against the real service with WireMock standing in for apicvr.dk, and fails if any field the
consumer relies on has been renamed, removed or retyped.

The pact files are exchanged either through a Pact Broker (a container, if you want the full
can-i-deploy workflow) or — simpler and adequate at five services — committed to the repository under
`pacts/`, with the provider verification running in the same CI pipeline. Start with committed
files; add the broker when independent repositories or independent release cadences make it
necessary.

### 22.2 Versioning policy

Write it down once and apply it mechanically:

| Contract | Versioning | Breaking-change process |
| --- | --- | --- |
| Public API (`/api/v1/...`) | URL version | New `/api/v2` alongside v1; v1 supported for ≥6 months; deprecation announced with a `Deprecation` header and a sunset date |
| Internal service contracts | No version in the URL | Expand-then-contract, always: (1) add the new field, deploy the provider; (2) consume it, deploy the consumer; (3) remove the old field, deploy the provider. Never (1) and (3) together |
| Events (`FirmaData.Messaging.Contracts`) | New type per breaking change | Publish both types during migration; consumers handle both; retire the old one once no consumer subscribes |
| Database schema | Migrations | Every migration backwards-compatible with the previously deployed application version |

Expand-then-contract is the rule that makes independent deployment actually work. Skipping step 2 —
deploying a consumer that needs a field the deployed provider does not yet send — is the single most
common cause of a failed microservice release.

**Exit criteria**

- [ ] Removing a field from `CompanyResource` fails the provider's verification in CI.
- [ ] The versioning policy is in the repository, not in someone's head.
- [ ] The public contract has not changed at any point in either guide.

---

## 23. Phase 23 — Chaos and load testing

**Goal:** the resilience settings from the base guide stop being assumptions.

Every timeout, retry count and circuit-breaker threshold in this system was chosen by reasoning.
Reasoning is a hypothesis. This phase tests it.

### 23.1 Toxiproxy

```yaml
  toxiproxy:
    image: ghcr.io/shopify/toxiproxy:2.11.0
    ports:
      - "18474:8474"   # control API
      - "18475:8475"   # the proxied statistics service
```

Point the enrichment service at the proxy instead of the service (`StatbankService__BaseUrl:
http://toxiproxy:8475/`) and inject faults through the control API:

```powershell
# Create the proxy
curl.exe -s -X POST http://localhost:18474/proxies -d '{\"name\":\"statbank\",\"listen\":\"0.0.0.0:8475\",\"upstream\":\"firmadata-statbank:8080\"}'

# 3 seconds of added latency -- inside the 18s attempt timeout, so requests should still succeed, slowly
curl.exe -s -X POST http://localhost:18474/proxies/statbank/toxics -d '{\"type\":\"latency\",\"attributes\":{\"latency\":3000}}'

# 25 seconds -- outside it, so the timeout must fire and degrade to SourceUnavailable
curl.exe -s -X POST http://localhost:18474/proxies/statbank/toxics/latency -d '{\"attributes\":{\"latency\":25000}}'

# Connection reset on half the requests -- the circuit breaker should open
curl.exe -s -X POST http://localhost:18474/proxies/statbank/toxics -d '{\"type\":\"reset_peer\",\"toxicity\":0.5}'

# Clean up
curl.exe -s -X DELETE http://localhost:18474/proxies/statbank/toxics/latency
```

The experiments worth running, each with a written hypothesis *before* it is run:

| Experiment | Hypothesis | Falsified if |
| --- | --- | --- |
| +3 s on statistics | Requests succeed, p95 rises ~3 s | Anything times out |
| +25 s on statistics | Attempt timeout fires; 200 + `SourceUnavailable` | Request hangs, or returns 500 |
| 50% resets on statistics | Circuit opens within ~10 requests; failures become instant | Circuit never opens, or opens on the first failure |
| Statistics down entirely | 200 + `Warning: 199` throughout | Any 5xx reaches the user |
| CVR +25 s | 503 + `Retry-After` | Request hangs past the frontend's 30 s budget |
| Bandwidth toxic on CVR | Large name searches degrade gracefully | Memory grows without bound |

Falsified hypotheses are the valuable outcome — each one is a resilience setting that was wrong and
would have been discovered in production instead.

### 23.2 Load

`ops/k6/lookup.js`:

```javascript
import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  stages: [
    { duration: '1m', target: 10 },   // ramp
    { duration: '3m', target: 50 },   // sustained
    { duration: '1m', target: 200 },  // spike -- should shed load via 429, not fall over
    { duration: '2m', target: 0 },    // recovery
  ],
  thresholds: {
    // These are the service's SLOs, asserted. A build that violates them fails.
    'http_req_duration{expected_response:true}': ['p(95)<1500'],
    'http_req_failed': ['rate<0.01'],
  },
};

const CVRS = ['16500836', '25313763', '35954716'];

export default function () {
  const cvr = CVRS[Math.floor(Math.random() * CVRS.length)];
  const res = http.get(`http://localhost:18080/api/v1/companies/${cvr}?year=2022`);

  check(res, {
    'not a server error': (r) => r.status < 500,
    // 429 under the spike is correct behaviour, not a failure
    'status is 200 or 429': (r) => r.status === 200 || r.status === 429,
  });

  sleep(1);
}
```

```powershell
docker run --rm -i --network host -v ${PWD}/ops/k6:/scripts grafana/k6 run /scripts/lookup.js
```

Watch the Grafana dashboard while it runs. What to look for, in order: does the cache hit ratio climb
(it should — the same three CVR numbers); does p95 stay flat as load rises (it should until a
resource saturates); which service's p95 rises first (that is your bottleneck); does the spike
produce 429s rather than timeouts (load shedding working) or 500s (it isn't).

**Exit criteria**

- [ ] All six chaos experiments run, with results recorded against their hypotheses.
- [ ] Any falsified hypothesis is fixed or documented as accepted.
- [ ] The k6 thresholds pass, and the numbers are recorded as the SLO baseline for Phase 24.
- [ ] The spike sheds load with 429s and recovers without a restart.

---

## 24. Phase 24 — Alerting and SLOs

**Goal:** a human is told about a problem before a user reports it, and is not woken for anything
else.

Dashboards are for investigating. Alerts are for interrupting. The distinction matters, because
alerting on everything a dashboard shows is how teams learn to ignore alerts.

```yaml
  alertmanager:
    image: prom/alertmanager:v0.28.0
    ports:
      - "19093:9093"
    volumes:
      - ./ops/alertmanager/alertmanager.yml:/etc/alertmanager/alertmanager.yml:ro
```

Wire it into Prometheus (`ops/prometheus/prometheus.microservices.yml`):

```yaml
alerting:
  alertmanagers:
    - static_configs:
        - targets: ["alertmanager:9093"]

rule_files:
  - /etc/prometheus/rules/*.yml
```

`ops/prometheus/rules/firmadata.yml`:

```yaml
groups:
  - name: firmadata-slo
    interval: 30s
    rules:
      # Recording rules first: the alert expressions below stay readable, and a dashboard and an
      # alert cannot disagree about what "error rate" means.
      - record: firmadata:request_error_rate:5m
        expr: |
          sum by (job) (rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m]))
          / sum by (job) (rate(http_server_request_duration_seconds_count[5m]))

      - record: firmadata:request_p95:5m
        expr: |
          histogram_quantile(0.95, sum by (le, job) (rate(http_server_request_duration_seconds_bucket[5m])))

  - name: firmadata-alerts
    rules:
      # Symptom-based, and only on the gateway: this is what a user experiences. Alerting on every
      # service's error rate would page four times for one incident.
      - alert: PublicApiErrorRateHigh
        expr: firmadata:request_error_rate:5m{job="gateway"} > 0.02
        for: 5m
        labels: { severity: page }
        annotations:
          summary: "Public API error rate above 2% for 5 minutes"
          runbook: "docs/runbook.md#publicapierrorratehigh"

      - alert: PublicApiLatencyHigh
        expr: firmadata:request_p95:5m{job="gateway"} > 2
        for: 10m
        labels: { severity: page }
        annotations:
          summary: "Public API p95 above 2s for 10 minutes"

      # Cause-based, ticket not page: the system is designed to survive this, so it is information,
      # not an emergency. Paging on it would train the on-call to ignore the page that matters.
      - alert: UpstreamCircuitOpen
        expr: firmadata_circuit_state > 1
        for: 2m
        labels: { severity: ticket }
        annotations:
          summary: "Circuit breaker open for {{ $labels.dependency }} in {{ $labels.job }}"

      - alert: StatisticsDegradationSustained
        expr: sum(rate(firmadata_enrichment_degraded_total[15m])) > 0.5
        for: 15m
        labels: { severity: ticket }
        annotations:
          summary: "More than half of enriched responses are degraded"

      - alert: CacheHitRatioCollapsed
        expr: |
          sum(rate(firmadata_cache_hits_total[30m]))
          / (sum(rate(firmadata_cache_hits_total[30m])) + sum(rate(firmadata_cache_misses_total[30m]))) < 0.3
        for: 30m
        labels: { severity: ticket }
        annotations:
          summary: "Statistics cache hit ratio below 30% — Redis down, or TTLs misconfigured"

      - alert: ServiceDown
        expr: up{job=~"gateway|enrichment|cvr|statbank"} == 0
        for: 2m
        labels: { severity: page }
        annotations:
          summary: "{{ $labels.job }} has been unreachable for 2 minutes"
```

Two principles are worth stating explicitly because they are what separate a useful alert set from
an ignored one:

* **Page on symptoms, ticket on causes.** `PublicApiErrorRateHigh` is what a user feels.
  `UpstreamCircuitOpen` is a cause the system already handles — worth investigating on Monday, not at
  03:00.
* **`for:` durations are not decoration.** They are what stops a 20-second blip from paging anyone.

Routing in `ops/alertmanager/alertmanager.yml` sends `severity: page` to PagerDuty/Opsgenie and
`severity: ticket` to a Slack channel or an issue tracker, with `group_by: [alertname, job]` and an
inhibition rule so `ServiceDown` suppresses that service's other alerts. One incident, one
notification.

**Exit criteria**

- [ ] Stopping the enrichment service fires `ServiceDown` within ~2 minutes and reaches the
      configured receiver.
- [ ] Every page-severity alert names a runbook section that exists.
- [ ] A 30-second blip fires nothing.
- [ ] Thresholds match the numbers measured in Phase 23, not numbers someone liked the look of.

---

## 25. Phase 25 — Supply chain and container hardening

**Goal:** the images are defensible — minimal, non-root, scanned, inventoried and signed.

### 25.1 Runtime hardening

Compose additions per service:

```yaml
    read_only: true
    tmpfs:
      - /tmp
    security_opt:
      - no-new-privileges:true
    cap_drop:
      - ALL
    deploy:
      resources:
        limits:
          cpus: "1.0"
          memory: 512M
        reservations:
          memory: 128M
```

* `read_only` + `tmpfs:/tmp` — .NET needs a writable temp directory and nothing else; a read-only
  root filesystem removes an entire class of persistence-after-compromise.
* `cap_drop: ALL` — the images already run as the non-root `app` user from the .NET base image and
  bind port 8080, so no capability is needed at all.
* **Memory limits are not optional.** Without one, a container with a runaway allocation takes the
  host down with it. With one, .NET's GC also sees the limit and adapts its heap.

### 25.2 Pin by digest

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0@sha256:<digest> AS build
FROM mcr.microsoft.com/dotnet/aspnet:8.0@sha256:<digest> AS final
```

`8.0` is a moving tag: the same Dockerfile produces different images on different days, which makes
"it worked yesterday" unfalsifiable. Pin the digest, and let Dependabot or Renovate raise the PR that
moves it — that way the update is a reviewed, dated change rather than a silent one.

Consider `mcr.microsoft.com/dotnet/aspnet:8.0-alpine` (smaller attack surface) or the chiselled
`-jammy-chiseled` variant (no shell, no package manager). Note the trade-off: chiselled images have
no `curl`, so the compose healthcheck must switch to .NET's own
`Microsoft.Extensions.Diagnostics.HealthChecks` endpoint polled from outside, or to a tiny compiled
health probe.

### 25.3 Scan, inventory, sign

The existing CI has Trivy as `continue-on-error: true` — informational. For production, split it:

```yaml
      - name: Trivy — fail on HIGH/CRITICAL with a fix available
        uses: aquasecurity/trivy-action@v0.36.0
        with:
          image-ref: ${{ steps.vars.outputs.sha_tag }}
          format: table
          severity: HIGH,CRITICAL
          ignore-unfixed: true      # do not block on vulnerabilities nobody can fix yet
          exit-code: "1"

      - name: Trivy — full report, informational
        continue-on-error: true
        uses: aquasecurity/trivy-action@v0.36.0
        with:
          image-ref: ${{ steps.vars.outputs.sha_tag }}
          format: table
          severity: LOW,MEDIUM
          exit-code: "0"

      - name: SBOM
        uses: anchore/sbom-action@v0
        with:
          image: ${{ steps.vars.outputs.sha_tag }}
          format: spdx-json
          artifact-name: sbom-${{ matrix.service }}.spdx.json

      - name: Sign the image (keyless, OIDC)
        run: cosign sign --yes ${{ steps.vars.outputs.sha_tag }}
```

`ignore-unfixed: true` is what makes the gate sustainable: blocking on vulnerabilities with no
available fix means the pipeline is red for reasons no one can act on, and a permanently red pipeline
is the same as no pipeline.

Also enable Dependabot for NuGet, GitHub Actions and Docker (`.github/dependabot.yml`), and add
`--locked-mode` restore with a committed `packages.lock.json` per project so a transitive dependency
cannot change silently between CI and production.

**Exit criteria**

- [ ] Every container runs read-only, non-root, with dropped capabilities and memory limits.
- [ ] Base images pinned by digest; Dependabot raises the updates.
- [ ] A HIGH/CRITICAL fixable vulnerability fails the build.
- [ ] SBOM published per image; images signed and verifiable with `cosign verify`.

---

## 26. Phase 26 — Production topology and runbook

**Goal:** the stack can be deployed, observed, and recovered by someone who did not build it.

### 26.1 Compose overrides

The demo settings and the production settings are separated by file, not by editing:

```
docker-compose.microservices.yml           # base — services, networks, dependencies
docker-compose.microservices.dev.yml       # ports published, Development environment, Swagger on
docker-compose.microservices.prod.yml      # replicas, limits, read-only, restart policy, no ports
```

```powershell
docker compose -f docker-compose.microservices.yml -f docker-compose.microservices.prod.yml up -d
```

`docker-compose.microservices.prod.yml`:

```yaml
services:
  firmadata-gateway:
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      Tracing__SampleRatio: "0.1"
    restart: unless-stopped
    deploy:
      replicas: 2
    logging:
      driver: json-file
      options:
        # Without rotation, container logs fill the disk and take the host down. This is the single
        # most common self-inflicted production outage on a compose-based deployment.
        max-size: "10m"
        max-file: "5"
```

Repeat the `restart`, `deploy` and `logging` blocks per service. `ASPNETCORE_ENVIRONMENT:
Production` also switches Swagger off, since the enrichment service gates it on `IsDevelopment()`.

### 26.2 Deployment procedure

Zero-downtime on plain Compose, per service, with the gateway's active health checks doing the
draining:

```powershell
# 1. Pull the new image (built and pushed by CI, tagged by SHA — never :latest in production)
docker compose -f docker-compose.microservices.yml -f docker-compose.microservices.prod.yml pull firmadata-cvr

# 2. Migrations first, if any, as a one-shot container that must exit 0
docker compose run --rm firmadata-enrichment-migrate

# 3. Recreate one service at a time; the healthcheck gates the next step
docker compose -f docker-compose.microservices.yml -f docker-compose.microservices.prod.yml up -d --no-deps firmadata-cvr

# 4. Verify before continuing
curl.exe -sf http://localhost:18080/health/ready
```

Deployment order matters, and follows the expand-then-contract rule from §22.2: **providers before
consumers** when a contract has grown; **consumers before providers** when a contract is being
narrowed. Deploying in the wrong order is a self-inflicted outage during a release that would
otherwise have been invisible.

Rollback is the same procedure with the previous SHA tag — which only works if images are tagged by
SHA and the previous tag still exists in GHCR. Verify the retention policy allows it.

### 26.3 Runbook

`docs/runbook.md`, one section per page-severity alert, each with the same four headings:

```markdown
## PublicApiErrorRateHigh

**What the user sees.** Errors on the search page, or 5xx from the public API.

**Check, in order.**
1. Grafana → FirmaData microservices → "Error rate by service". Which job is red?
   If only `gateway` is red, the problem is the gateway or its cluster health.
   If `enrichment` is red too, follow it down the chain.
2. `up{job=~"..."}` — is anything down?
3. Grafana → Explore → Tempo → filter by error, pick a failing trace. Which span failed?
4. Loki: `{service="firmadata-enrichment"} | json | level = "Error"` for the exception.

**Most likely causes.**
- One upstream (apicvr.dk) down → circuit open → 503 to users. Confirm with
  `firmadata_circuit_state{dependency="cvr"}`. Nothing to do but wait; the circuit closes itself.
- A deploy in the last 30 minutes → roll back to the previous SHA tag (§26.2).
- Redis down → not this alert; it degrades latency, not correctness.

**Mitigation.** Roll back if a deploy correlates. Otherwise the degradation is by design: the
statistics source failing yields 200 + Warning, and only the CVR source failing yields 5xx.

**Escalate** to the platform owner if the CVR source has been down for more than 30 minutes —
that is an upstream conversation, not a code fix.
```

Sections to write, matching the alerts in §24: `PublicApiErrorRateHigh`, `PublicApiLatencyHigh`,
`ServiceDown`, `UpstreamCircuitOpen`, `StatisticsDegradationSustained`, `CacheHitRatioCollapsed`,
plus non-alert procedures: deploy, rollback, secret rotation, database restore, and scaling up under
load.

### 26.4 Operational readiness checklist

Before anyone calls this production-ready, every line must be checkable by someone other than the
author:

**Observability**
- [ ] Every service exposes `/metrics`, `/health/live`, `/health/ready`
- [ ] p95 and error rate per service on a dashboard
- [ ] Traces span every hop; logs carry trace and correlation ids
- [ ] Alerts route to a real receiver, tested end to end

**Resilience**
- [ ] Timeout budgets increase outward; verified in Phase 23
- [ ] Retries do not multiply across layers
- [ ] Every dependency has a documented degradation behaviour
- [ ] Cache and audit database are optional to the request path

**Security**
- [ ] No anonymous access to business endpoints
- [ ] No secret in git, in an image, or in a compose file
- [ ] Containers non-root, read-only, capability-dropped, memory-limited
- [ ] Images scanned, signed and pinned by digest

**Operations**
- [ ] Deploy and rollback documented and rehearsed
- [ ] Migrations run as a deployment step
- [ ] Log rotation configured
- [ ] Database backup taken and a **restore actually tested** — an untested backup is not a backup
- [ ] Runbook exists, and a page-severity alert links to a section that exists

**Contracts**
- [ ] Contract tests fail the provider's build on a breaking change
- [ ] Versioning policy written down
- [ ] The public API contract is unchanged since the monolith

---

## Appendix — Full port map

| Service | Container | Host (microservices stack) |
| --- | --- | --- |
| Gateway | 8080 | 18080 |
| CVR service | 8080 | 18081 (dev only) |
| Statistics service | 8080 | 18082 (dev only) |
| Enrichment service | 8080 | 18083 (dev only) |
| Web (MVC) | 8080 | 18090 |
| Grafana | 3000 | 13000 |
| Loki | 3100 | 13100 |
| Tempo (API / OTLP) | 3200 / 4317 | 13200 / 14317 |
| RabbitMQ (AMQP / UI) | 5672 / 15672 | 15680 / 15681 |
| PostgreSQL | 5432 | 15432 |
| Redis | 6379 | 16379 |
| Keycloak | 8080 | 18180 |
| Vault | 8200 | 18200 |
| Toxiproxy (API / proxy) | 8474 / 8475 | 18474 / 18475 |
| Prometheus | 9090 | 19090 |
| Alertmanager | 9093 | 19093 |

Fifteen containers on one host. That number is itself the argument for moving to a real orchestrator
— which is the honest place for this guide to end.

## Appendix — What each phase actually costs

| Phase | Containers added | Ongoing operational cost |
| --- | --- | --- |
| 13 Redis | 1 | Memory tuning, eviction policy, one more thing that can be slow |
| 14 Scaling | 0 (replicas) | Statelessness discipline forever |
| 15 Messaging | 2 | Queue depth monitoring, dead-letter triage, message versioning |
| 16 Tracing | 1 | Trace storage growth; sampling decisions |
| 17 Logging | 1 | Log volume and retention costs |
| 18 Auth | 1 | Identity provider ownership, token lifetimes, key rotation |
| 19 Secrets | 1 | Unseal procedure, rotation runbook |
| 20 Quotas | 0 | Per-client limit tuning and support requests about 429s |
| 21 Persistence | 1 | Backups, restore tests, migrations, disk growth |
| 22 Contracts | 0 | A pact to maintain per consumer/provider pair |
| 23 Chaos/load | 1 | Re-run before each release, or it decays |
| 24 Alerting | 1 | Alert tuning; every false page erodes trust |
| 25 Hardening | 0 | Dependency and base-image update flow |
| 26 Runbook | 0 | Keeping it true as the system changes |

**The summary worth remembering:** the base guide's split cost ~1,200 lines and three new failure
modes. Making that split *productionable* costs ten more containers and a permanent operational
burden. Both are worth it at the right scale — and neither is worth it at FirmaData's current one,
which is exactly what ADR-0001 concluded.
