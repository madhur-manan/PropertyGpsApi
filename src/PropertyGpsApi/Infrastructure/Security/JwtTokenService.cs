using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PropertyGpsApi.Features.Auth;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Infrastructure.Security;

public interface IJwtTokenService
{
    (string Token, DateTimeOffset ExpiresAt) Issue(Officer officer);
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
    public const string CorporationId = "corpId";
    public const string ZoneId = "zoneId";
    public const string WardId = "wardId";
    public const string Mobile = "mobile";
}

internal sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock) : IJwtTokenService
{
    public (string Token, DateTimeOffset ExpiresAt) Issue(Officer officer)
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
            new("name", officer.Name ?? "")
        };

        if (officer.CorporationId is { } corp) claims.Add(new Claim(GpsClaims.CorporationId, corp.ToString()));
        if (officer.ZoneId is { } zone) claims.Add(new Claim(GpsClaims.ZoneId, zone.ToString()));
        // A zone-level officer (role 125) legitimately has no ward, so this claim is absent
        // rather than zero - callers must treat "no ward" as a real state.
        if (officer.WardId is { } ward) claims.Add(new Claim(GpsClaims.WardId, ward.ToString()));

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
}
