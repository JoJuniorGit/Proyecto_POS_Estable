using System;

namespace Backend.API.Attributes;

/// <summary>
/// Enforces immediate database verification of the user's SecurityStamp (bypassing the micro-cache grace window).
/// Required for sensitive and monetary operations (checkouts, closures, cash drawer expenses).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireSecurityStampValidationAttribute : Attribute
{
}
