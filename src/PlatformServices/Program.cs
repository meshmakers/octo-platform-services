using Meshmakers.Octo.Backend.PlatformServices.Options;
using Meshmakers.Octo.Services.Observability;
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

    builder.Services.AddControllers();

    // NLog: Setup NLog for Dependency injection
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Trace);
    builder.Host.UseNLog();

    // additional providers here needed.
    // allow environment variables to override values from other providers.
    builder.Configuration.AddEnvironmentVariables("OCTO_").AddCommandLine(args).AddUserSecrets(typeof(Program).Assembly, true);

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

    var app = builder.Build();

    app.MapObservability();

    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
    }
    app.UseCors();

    app.UseRouting();
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
