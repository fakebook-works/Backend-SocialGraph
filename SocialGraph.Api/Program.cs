using StackExchange.Redis;

using HotChocolate.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Migrations;
using SocialGraph.Api.Infrastructure.Outbox;
using SocialGraph.Api.Service;
using SocialGraph.Api.SubGraphQL;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddFakebookServiceDefaults(builder.Configuration, "fakebook-social-graph");

builder.Services.AddControllers();
builder.Services.AddInternalRequestSigning(
    builder.Configuration,
    "InternalServices:SocialGraph:SharedSecret",
    InternalCallerAuthentication.ServiceSecretHeaderName);
var externalServicesClient = builder.Services.AddHttpClient("external-services", client =>
{
    var timeoutSeconds = Math.Clamp(
        builder.Configuration.GetValue<int?>("InternalServices:TimeoutSeconds") ?? 10,
        1,
        60);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
});
externalServicesClient.AddStandardResilienceHandler(options => options.Retry.DisableForUnsafeHttpMethods());
externalServicesClient.AddHttpMessageHandler<InternalRequestSigningHandler>();

// Content embeddings can legitimately take longer than the short control-plane
// calls (the Recommendation worker may download bounded media and run the model
// on its first request). Keep this as a separate, explicitly bounded client so a
// slow embedding never silently changes the timeout for Auth/Search/Notification
// or any other internal target. Unsafe-method retries remain disabled by the
// standard resilience pipeline; the outbox supplies idempotency and retry policy.
var recommendationContentTimeoutSeconds = Math.Clamp(
    builder.Configuration.GetValue<int?>("InternalServices:Recommendation:ContentTimeoutSeconds") ?? 180,
    30,
    300);
var recommendationContentClient = builder.Services
    .AddHttpClient("recommendation-content", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(recommendationContentTimeoutSeconds);
    });
// Use a dedicated pipeline for this one bounded, idempotent outbox operation.
// The circuit-breaker sampling window must be at least twice the attempt
// timeout or Options validation aborts the service.
recommendationContentClient.AddStandardResilienceHandler(options =>
{
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(recommendationContentTimeoutSeconds);
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(recommendationContentTimeoutSeconds);
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(recommendationContentTimeoutSeconds * 2);
    options.Retry.DisableForUnsafeHttpMethods();
});
recommendationContentClient.AddHttpMessageHandler<InternalRequestSigningHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITrustedCallerAccessor, TrustedCallerAccessor>();
builder.Services.Configure<SocialGraphCacheOptions>(options =>
{
    options.Mode = Environment.GetEnvironmentVariable("CACHE_MODE")
        ?? builder.Configuration[$"{SocialGraphCacheOptions.SectionName}:Mode"]
        ?? "auto";
});

builder.Services.AddDbContext<MyDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgreSQL")));

// 1. Đăng ký kết nối Redis (Dòng code của bạn)
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var connectionString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
    var configuration = ConfigurationOptions.Parse(connectionString);
    configuration.AbortOnConnectFail = false;
    configuration.ConnectRetry = 0;
    configuration.ConnectTimeout = Math.Min(configuration.ConnectTimeout, 750);
    configuration.AsyncTimeout = Math.Min(configuration.AsyncTimeout, 750);
    configuration.SyncTimeout = Math.Min(configuration.SyncTimeout, 750);
    return ConnectionMultiplexer.Connect(configuration);
});

builder.Services.AddScoped<IObjectService, ObjectService>();
builder.Services.AddScoped<IAssociationService, AssociationService>();
builder.Services.AddScoped<IBlockVisibilityService, BlockVisibilityService>();
builder.Services.Configure<IntegrationOutboxOptions>(
    builder.Configuration.GetSection(IntegrationOutboxOptions.SectionName));
builder.Services.AddSingleton<IOutboxPayloadProtector, OutboxPayloadProtector>();
builder.Services.AddScoped<IIntegrationOutboxStore, PostgresIntegrationOutboxStore>();
builder.Services.AddScoped<IExternalServiceClient, IntegrationOutboxPublisher>();
builder.Services.AddScoped<IExternalServiceTransport, ExternalServiceClient>();
builder.Services.AddScoped<IIntegrationOutboxDispatcher, IntegrationOutboxDispatcher>();
builder.Services.AddSingleton<IIntegrationOutboxMessageProcessor, IntegrationOutboxMessageProcessor>();
builder.Services.AddScoped<IUserProvisioningCoordinator, UserProvisioningCoordinator>();
builder.Services.AddScoped<IUserGraphService, UserGraphService>();
builder.Services.AddScoped<IAuthenticationContactClient, AuthenticationContactClient>();
builder.Services.AddScoped<IGroupGraphService, GroupGraphService>();
builder.Services.AddScoped<IContentGraphService, ContentGraphService>();
builder.Services.AddScoped<ICandidateService, CandidateService>();
builder.Services.AddScoped<ISocialReadModelService, SocialReadModelService>();
builder.Services.AddScoped<IMessagingPermissionService, MessagingPermissionService>();
builder.Services.AddScoped<IMediaOwnershipGuard, UploadMediaOwnershipGuard>();
builder.Services.AddDataLoader<HomePostByIdDataLoader>();
builder.Services.AddDataLoader<FederatedUserByIdDataLoader>();
builder.Services.AddHostedService<OutboxSchemaHostedService>();
builder.Services.AddHostedService<IntegrationOutboxWorker>();
builder.Services.AddHostedService<StoryCleanupBackgroundService>();

// 3. Đăng ký bộ điều phối GraphQL Subgraph
builder.Services
    .AddGraphQLServer()
    .AddErrorFilter<MediaOwnershipErrorFilter>()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>()
    .AddType<RecommendationItemResult>()
    .AddTypeExtension<RecommendationItemResolvers>()
    .AddType<ReelRecommendationItemResult>()
    .AddTypeExtension<ReelRecommendationItemResolvers>()
    .AddType<FeedPostDetailResult>()
    .AddType<ReelDetailResult>()
    .AddType<GroupPostDetailResult>()
    .AddType<NormalStoryResult>()
    .AddType<FeedPostShareStoryResult>()
    .AddType<ReelShareStoryResult>()
    .AddType<FeedPostSharedSourceResult>()
    .AddType<ReelSharedSourceResult>()
    .AddApolloFederation();

var app = builder.Build();
if (AssociationContractMigrationCommand.IsRequested(args))
{
    Environment.ExitCode = await AssociationContractMigrationCommand.RunAsync(
        args,
        app.Configuration,
        app.Logger);
    return;
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<InternalRequestSignatureMiddleware>();
app.UseMiddleware<InternalApiAuthenticationMiddleware>();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet(
    "/health/ready",
    async (MyDbContext dbContext, IConnectionMultiplexer redis, IInternalNonceStore nonceStore, CancellationToken cancellationToken) =>
    {
        var readiness = await HealthProbe.CheckReadinessAsync(dbContext, redis, cancellationToken);
        var securityRedis = await nonceStore.IsAvailableAsync(cancellationToken);
        return Results.Json(
            new
            {
                status = readiness.Ready ? "ready" : "not-ready",
                postgres = readiness.PostgreSql,
                redis = readiness.Redis,
                securityRedis
            },
            statusCode: readiness.Ready && securityRedis
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable);
    });
app.MapGraphQL("/graphql").WithOptions(options =>
{
    options.Batching = AllowedBatching.All;
    options.MaxBatchSize = ContentGraphService.MaxPostDetailIds;
});
app.MapControllers();
app.RunWithGraphQLCommands(args);
