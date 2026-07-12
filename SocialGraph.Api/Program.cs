using StackExchange.Redis;

using HotChocolate.AspNetCore;
using Microsoft.EntityFrameworkCore;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Infrastructure;
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

builder.Services.AddDbContext<MyDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgreSQL")));

// 1. Đăng ký kết nối Redis (Dòng code của bạn)
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var configuration = builder.Configuration.GetConnectionString("Redis");
    return ConnectionMultiplexer.Connect(configuration!);
});

builder.Services.AddScoped<IObjectService, ObjectService>();
builder.Services.AddScoped<IAssociationService, AssociationService>();
builder.Services.AddScoped<IExternalServiceClient, ExternalServiceClient>();
builder.Services.AddScoped<IUserGraphService, UserGraphService>();
builder.Services.AddScoped<IGroupGraphService, GroupGraphService>();
builder.Services.AddScoped<IContentGraphService, ContentGraphService>();
builder.Services.AddScoped<ICandidateService, CandidateService>();
builder.Services.AddDataLoader<HomePostByIdDataLoader>();
builder.Services.AddHostedService<StoryCleanupBackgroundService>();

// 3. Đăng ký bộ điều phối GraphQL Subgraph
builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>()
    .AddType<RecommendationItemResult>()
    .AddTypeExtension<RecommendationItemResolvers>()
    .AddType<FeedPostDetailResult>()
    .AddType<GroupPostDetailResult>()
    .AddType<NormalStoryResult>()
    .AddType<FeedPostShareStoryResult>()
    .AddType<ReelShareStoryResult>()
    .AddType<FeedPostSharedSourceResult>()
    .AddType<ReelSharedSourceResult>()
    .AddApolloFederation();

var app = builder.Build();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<InternalApiAuthenticationMiddleware>();
app.MapGraphQL("/graphql").WithOptions(options =>
{
    options.Batching = AllowedBatching.All;
    options.MaxBatchSize = ContentGraphService.MaxPostDetailIds;
});
app.MapControllers();
app.RunWithGraphQLCommands(args);
