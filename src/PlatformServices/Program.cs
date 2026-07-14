using System.IdentityModel.Tokens.Jwt;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.PlatformServices;
using Meshmakers.Octo.Backend.PlatformServices.Configuration;
using Meshmakers.Octo.Backend.PlatformServices.Options;
using Meshmakers.Octo.Backend.PlatformServices.Routing;
using Meshmakers.Octo.Backend.PlatformServices.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Engine.Configuration.DependencyInjection;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Observability;
using Meshmakers.Octo.Services.Swagger.Configuration;
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
    // Bind blueprint variable context (octo.version/environment/systemTenantId) so the default
    // IBlueprintVariableProvider surfaces values from helm-injected OCTO_BLUEPRINTS__* environment
    // variables instead of falling back to defaults — drives ${octo.environmentMode} /
    // ${octo.isSystemTenant} in the System.UI / System.TenantMode seeds.
    builder.Services.Configure<OctoBlueprintVariablesOptions>(options =>
        builder.Configuration.GetSection(OctoBlueprintVariablesOptions.SectionName).Bind(options));

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
    // ITenantBlueprintHistory used by the read-only observability endpoints AND by the
    // System.UI service-managed blueprint apply.
    builder.Services.AddRuntimeEngine()
        .AddMongoDbRuntimeRepository()
        .AddMongoBlueprintSupport();

    // Tenant lifecycle host — registers the distribution event hub tenant-event consumers
    // (PosCreateTenant / PosUpdateTenant) plus the cold-start initialization services that
    // drive IDefaultConfigurationCreatorService.SetupAsync / StartDeferredTenantsAsync. Required
    // since platform-services owns the System.UI service-managed blueprints (Phase 4): they are
    // seeded on tenant create / attach / restore via these events. No identity-data command
    // client is registered — the blueprint-only creator seeds no identity data.
    builder.Services.ConfigureOptions<ConfigureDistributionEventHubOptions>();
    builder.Services.AddOctoServiceInfrastructure("PlatformServices");

    // Swagger UI / OpenAPI (AB#4388) — shared package, same pattern as octo-mcp-service.
    // The UI's OAuth authorization-code flow uses the dedicated
    // octo-platformServices-swagger client seeded by System.Identity.Bootstrap.
    builder.Services.ConfigureOptions<ConfigureOctoOpenApiOptions>();
    builder.Services.AddOctoApiVersioningAndDocumentation(options =>
    {
        options.Scopes = new Dictionary<string, string>
        {
            {
                CommonConstants.OctoApiFullAccess,
                CommonConstants.OctoApiFullAccessDisplayName
            }
        };

        options.XmlDocDataTransferObjectAssemblies = [typeof(Program).Assembly];
        options.XmlDocOperationAssemblies = [typeof(Program).Assembly];

        options.ApiTitle = "OctoMesh Platform Services API";
        options.ApiDescription =
            "Tenant configuration discovery and platform observability endpoints (tenants, blueprints, service drift).";

        options.ClientId = PlatformServicesConstants.PlatformServicesSwaggerClientId;
        options.AppName = "OctoMesh Platform Services Swagger Client";
    }).AddVersion();
    builder.Services.AddTransient<IDefaultConfigurationCreatorService, DefaultConfigurationCreatorService>();

    // System.UI CK model + the service-managed blueprints (cockpit dashboards + the
    // cross-cutting System.TenantMode seed). DefaultConfigurationCreatorService applies every
    // System.UI.* blueprint (plus the allowlisted System.TenantMode) per tenant; each blueprint's
    // `requires:` block decides which tenants actually receive it.
    builder.Services.AddCkModelSystemUIV2();
    // Auto-import System.UI at its embedded version into every tenant on resolve (engine descriptor
    // mechanism), decoupled from the cockpit blueprint floors: bumping ConstructionKit/ckModel.yaml
    // now propagates to all tenants — no ckModelDependencies bump required.
    builder.Services.AddSingleton<IServiceManagedCkModelDescriptor>(
        _ => new ServiceManagedCkModelDescriptor(
            SystemUiCkModel.Generated.System.UI.v2.SystemUICkIds.CkModelId));
    builder.Services.AddBlueprintSystemUISystemCockpitV1();
    builder.Services.AddBlueprintSystemUITenantCockpitV1();
    builder.Services.AddBlueprintSystemTenantModeV1();

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
    app.UseStaticFiles();
    app.UseOctoApiVersioningAndDocumentation();
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
