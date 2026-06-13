using System.IdentityModel.Tokens.Jwt;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.PlatformServices;
using Meshmakers.Octo.Backend.PlatformServices.Options;
using Meshmakers.Octo.Backend.PlatformServices.Routing;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Runtime.Engine.Configuration.DependencyInjection;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Observability;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using NLog;
using NLog.Web;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

// NLog: setup the logger first to catch all errors
var nLogFactory = LogManager.Setup().RegisterNLogWeb().LoadConfigurationFromFile("nlog.config").LogFactory;
var logger = nLogFactory.GetCurrentClassLogger();

try
{
    logger.Debug("init main");

    var builder = WebApplication.CreateBuilder(args);

    builder.AddObservability();

    builder.Services.Configure<PlatformServiceUrlsOptions>(options =>
        builder.Configuration.GetSection("PlatformServices").Bind(options));
    builder.Services.Configure<OctoSystemConfiguration>(options =>
        builder.Configuration.GetSection("System").Bind(options));

    builder.Services.AddControllers();

    // NLog: Setup NLog for Dependency injection
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Trace);
    builder.Host.UseNLog();

    builder.Configuration.AddEnvironmentVariables("OCTO_").AddCommandLine(args)
        .AddUserSecrets(typeof(Program).Assembly, true);

    // The _configuration endpoint is consumed by browser SPAs and Excel-hosted
    // add-ins from arbitrary origins. The response carries no credentials, so
    // AllowAnyOrigin is safe and required.
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy => policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod());
    });

    // Tenant-id route constraint — required by every Phase-2 Step 6 endpoint that
    // includes {tenantId} in its route, so the constraint name matches the rest of
    // the OctoMesh services (asset-repo, bot, identity, ...).
    builder.Services.Configure<RouteOptions>(options =>
        options.ConstraintMap.Add("tenantId", typeof(TenantIdRouteConstraint)));

    // MongoDB runtime engine — provides ISystemContext (tenant enumeration + per-tenant
    // repository lookup) and IBlueprintService / ITenantBlueprintInstallations /
    // ITenantBlueprintHistory used by the read-only observability endpoints.
    builder.Services.AddRuntimeEngine()
        .AddMongoDbRuntimeRepository()
        .AddMongoBlueprintSupport();

    // JWT Bearer authentication — tokens validated against the local Identity service.
    // Browser-flow cookie / OIDC scheme is intentionally omitted; this is a backend
    // admin API consumed by service accounts and the future Refinery Studio dashboard
    // page (which holds its own bearer token from the front-end auth library).
    JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            var authorityUrl = builder.Configuration["PlatformServices:AuthorityUrl"]?.EnsureEndsWith("/")
                               ?? "https://localhost:5003/";
            options.Authority = authorityUrl;
            options.TokenValidationParameters.ValidateAudience = false;
            // Pin ValidIssuer so validation does not need the OIDC discovery document
            // mid-rolling-update; identity / asset-repo / bot all do the same.
            options.TokenValidationParameters.ValidIssuer = authorityUrl;
        });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy(PlatformServicesConstants.PlatformServicesAdminPolicy, policy =>
        {
            policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
            policy.RequireAuthenticatedUser();
            policy.RequireClaim(InfrastructureCommon.ClaimScope,
                CommonConstants.OctoApiFullAccess);
        });
    });

    var app = builder.Build();

    app.MapObservability();

    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
    }
    app.UseCors();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    //NLog: catch setup errors
    logger.Error(ex, "Stopped program because of exception");
    throw;
}
finally
{
    // Ensure to flush and stop internal timers/threads before application-exit (Avoid segmentation fault on Linux)
    LogManager.Shutdown();
}
