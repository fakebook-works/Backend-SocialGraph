using StackExchange.Redis;

using HotChocolate.AspNetCore;
using Microsoft.EntityFrameworkCore;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Infrastructure;
using SocialGraph.Api.Migrations;
using SocialGraph.Api.Infrastructure.Outbox;
using SocialGraph.Api.Service;
using SocialGraph.Api.SubGraphQL;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpClient("external-services", client =>
{
    var timeoutSeconds = Math.Clamp(
        builder.Configuration.GetValue<int?>("InternalServices:TimeoutSeconds") ?? 10,
        1,
        60);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
});
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
builder.Services.Configure<IntegrationOutboxOptions>(
    builder.Configuration.GetSection(IntegrationOutboxOptions.SectionName));
builder.Services.AddSingleton<IOutboxPayloadProtector, OutboxPayloadProtector>();
builder.Services.AddScoped<IIntegrationOutboxStore, PostgresIntegrationOutboxStore>();
builder.Services.AddScoped<IExternalServiceClient, IntegrationOutboxPublisher>();
builder.Services.AddScoped<IExternalServiceTransport, ExternalServiceClient>();
builder.Services.AddScoped<IIntegrationOutboxDispatcher, IntegrationOutboxDispatcher>();
builder.Services.AddSingleton<IIntegrationOutboxMessageProcessor, IntegrationOutboxMessageProcessor>();
builder.Services.AddScoped<IUserGraphService, UserGraphService>();
builder.Services.AddScoped<IGroupGraphService, GroupGraphService>();
builder.Services.AddScoped<IContentGraphService, ContentGraphService>();
builder.Services.AddScoped<ICandidateService, CandidateService>();
builder.Services.AddScoped<ISocialReadModelService, SocialReadModelService>();
builder.Services.AddScoped<IMessagingPermissionService, MessagingPermissionService>();
builder.Services.AddDataLoader<HomePostByIdDataLoader>();
builder.Services.AddHostedService<OutboxSchemaHostedService>();
builder.Services.AddHostedService<IntegrationOutboxWorker>();
builder.Services.AddHostedService<StoryCleanupBackgroundService>();

// 3. Đăng ký bộ điều phối GraphQL Subgraph
builder.Services
    .AddGraphQLServer()
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
app.UseMiddleware<InternalApiAuthenticationMiddleware>();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet(
    "/health/ready",
    async (MyDbContext dbContext, IConnectionMultiplexer redis, CancellationToken cancellationToken) =>
    {
        var readiness = await HealthProbe.CheckReadinessAsync(dbContext, redis, cancellationToken);
        return Results.Json(
            new
            {
                status = readiness.Ready ? "ready" : "not-ready",
                postgres = readiness.PostgreSql,
                redis = readiness.Redis
            },
            statusCode: readiness.Ready
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
