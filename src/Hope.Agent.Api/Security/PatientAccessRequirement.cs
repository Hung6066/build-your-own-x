using Microsoft.AspNetCore.Authorization;

namespace Hope.Agent.Api.Security;

/// <summary>
/// Authorization requirement — caller must either hold the <c>admin</c>/<c>system</c>
/// role, or have the target patient/user id present in the <c>patients</c> claim.
/// Closes the broad-BOLA gap (C2) where any authenticated clinician could access
/// any patient's data via a hand-crafted route value or query parameter.
/// </summary>
public sealed class PatientAccessRequirement : IAuthorizationRequirement
{
    /// <summary>Name of the route value / query parameter holding the target id.</summary>
    public string RouteValueName { get; }

    public PatientAccessRequirement(string routeValueName = "userId")
    {
        RouteValueName = routeValueName;
    }
}
