using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Models;

namespace PropertyGpsApi.Infrastructure.Security;

public interface IJwtTokenService
{
    /// <summary>
    /// A token for this officer, carrying <paramref name="sessionId"/> as its sid claim so
    /// every request can be checked against dbo.GpsOfficerSession (one phone per officer).
    /// </summary>
    (string Token, DateTimeOffset ExpiresAt) Issue(Officer officer, Guid sessionId);
}

/// <summary>
/// Claim names used across the API. GpsClaims.UserId is read on every authenticated
/// request, so the officer's identity always comes from the token and never from the
/// request body - a client must not be able to name itself.
/// </summary>
public static class GpsClaims
{
    public const string UserId = "userId";
    public const string RoleId = "roleId";
    public const string Mobile = "mobile";

    /// <summary>
    /// The ULB code and ward number of the officer's FIRST jurisdiction, both as strings.
    /// Informational: a ward officer's own-ward checks read every mapped ward from the master
    /// tables (IOfficerService.MappedWardsAsync), since a token can carry only one.
    /// </summary>
    public const string UlbCode = "ulb";
    public const string WardNumber = "ward";

    // No longer issued: Property GPS works by ULB + ward number. Tokens issued before the
    // change still carry them, and nothing reads them.
    public const string CorporationId = "corpId";
    public const string ZoneId = "zoneId";
    public const string WardId = "wardId";

    /// <summary>The sign-in session (dbo.GpsOfficerSession.SessionId), as a GUID string.</summary>
    public const string SessionId = "sid";
}

internal sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock) : IJwtTokenService
{
    public (string Token, DateTimeOffset ExpiresAt) Issue(Officer officer, Guid sessionId)
    {
        var jwt = options.Value;
        var expiresAt = clock.GetUtcNow().AddMinutes(jwt.ExpiryMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, officer.OfficerId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(GpsClaims.UserId, officer.OfficerId.ToString()),
            new(GpsClaims.RoleId, officer.RoleId.ToString()),
            new(GpsClaims.Mobile, officer.Mobile ?? ""),
            new("name", officer.Name ?? ""),
            new(GpsClaims.SessionId, sessionId.ToString("D"))
        };

        // The first jurisdiction, else the officer's own ULB and ward. An officer above ward
        // level legitimately has no ward, so a claim is absent rather than empty - callers
        // must treat "no ward" as a real state.
        var (ulb, ward) = TokenWard(officer);
        if (ulb is { } u) claims.Add(new Claim(GpsClaims.UlbCode, u.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (ward is { } w) claims.Add(new Claim(GpsClaims.WardNumber, w));

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(jwt.KeyBytes), SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: jwt.Issuer,
            audience: jwt.Audience,
            claims: claims,
            notBefore: clock.GetUtcNow().UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    /// <summary>
    /// The ULB and ward the token carries: the first jurisdiction - the one the sign-in reply
    /// shows on top - or, with none, the officer's own. A ward is only taken with its ULB.
    /// </summary>
    internal static (int? UlbCode, string? WardNumber) TokenWard(Officer officer)
    {
        if (officer.Jurisdictions.FirstOrDefault(j => j.UlbCode is > 0) is { } first)
            return (first.UlbCode, WardNumbers.Clean(first.WardNumber));
        return officer.UlbCode is > 0
            ? (officer.UlbCode, WardNumbers.Clean(officer.WardNumber))
            : (null, null);
    }
}
