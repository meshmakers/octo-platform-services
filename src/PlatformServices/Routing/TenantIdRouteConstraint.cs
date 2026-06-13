namespace Meshmakers.Octo.Backend.PlatformServices.Routing;

/// <summary>
///     Route constraint that matches any non-empty string for the <c>{tenantId}</c> segment.
///     Used by Phase-2 Step 6 system-admin endpoints so tenant ids in routes can be parsed
///     uniformly against the same constraint name <c>tenantId</c> the rest of the OctoMesh
///     services use (asset-repo, bot, identity, …).
/// </summary>
internal class TenantIdRouteConstraint : IRouteConstraint
{
    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey,
        RouteValueDictionary values, RouteDirection routeDirection)
    {
        return values.TryGetValue(routeKey, out var value)
               && value is string s
               && !string.IsNullOrWhiteSpace(s);
    }
}
