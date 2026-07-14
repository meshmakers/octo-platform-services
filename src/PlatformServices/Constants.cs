namespace Meshmakers.Octo.Backend.PlatformServices;

/// <summary>
///     Platform-services constants — policy names, scope identifiers, and well-known
///     keys used to project drift information for backend services whose CK models
///     are not (yet) packaged as blueprints (concept §8.3 decision).
/// </summary>
internal static class PlatformServicesConstants
{
    /// <summary>
    ///     Authorization policy for every Phase-2 Step 6 observability endpoint. Requires
    ///     <see cref="Octo.Communication.Contracts.CommonConstants.OctoApiFullAccess"/> on
    ///     the access token's scope claim — drift is a platform-operator concern, no
    ///     per-tenant self-service surface (concept §8.2 decision).
    /// </summary>
    public const string PlatformServicesAdminPolicy = nameof(PlatformServicesAdminPolicy);

    /// <summary>
    ///     Client ID for the platform-services Swagger UI client (Authorization Code Flow).
    ///     Seeded by the <c>System.Identity.Bootstrap</c> blueprint (AB#4388).
    /// </summary>
    public const string PlatformServicesSwaggerClientId = "octo-platformServices-swagger";
}
