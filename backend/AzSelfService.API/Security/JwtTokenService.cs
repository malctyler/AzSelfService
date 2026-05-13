using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AzSelfService.API.Data.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AzSelfService.API.Security;

public static class AppRoles
{
    public const string Admin = "admin";
    public const string Customer = "customer";
}

public sealed class JwtTokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    public (string token, DateTime expiresAtUtc) GenerateToken(UserEntity user)
    {
        var role = GetRoleForUser(user);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiresAtUtc = DateTime.UtcNow.AddHours(_options.ExpirationHours);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new("customer_id", user.CustomerId.ToString()),
            new("username", user.Username),
            new(ClaimTypes.Role, role),
            new("role", role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var tokenDescriptor = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAtUtc,
            signingCredentials: credentials
        );

        return (new JwtSecurityTokenHandler().WriteToken(tokenDescriptor), expiresAtUtc);
    }

    public static string GetRoleForUser(UserEntity user)
        => string.Equals(user.Username, AppRoles.Admin, StringComparison.OrdinalIgnoreCase)
            ? AppRoles.Admin
            : AppRoles.Customer;
}

public sealed class JwtOptions
{
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int ExpirationHours { get; set; } = 24;
}