using StackExchange.Redis;

using Microsoft.EntityFrameworkCore;
using SocialGraph.Api.Contracts;
using SocialGraph.Api.Database;
using SocialGraph.Api.Service;
using SocialGraph.Api.SubGraphQL;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHttpClient("external-services");

builder.Services.AddDbContext<MyDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("PostgreSQL")));

// 1. Đăng ký kết nối Redis (Dòng code của bạn)
builder.Services.AddSingleton<IConnectionMultiplexer>(sp => {
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

// 3. Đăng ký bộ điều phối GraphQL Subgraph
builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>()
    .AddType<FeedPostSharedSourceResult>()
    .AddType<GroupPostSharedSourceResult>()
    .AddType<ReelSharedSourceResult>()
    .AddApolloFederation();

var app = builder.Build();
app.MapGraphQL("/graphql"); // Mở cửa duy nhất
app.MapControllers();
app.RunWithGraphQLCommands(args);
