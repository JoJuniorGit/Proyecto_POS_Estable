using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Core.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Backend.API.Services;

public class TokenService : ITokenService
{
    private readonly SymmetricSecurityKey _securityKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _expiryMinutes;

    public TokenService(IConfiguration configuration)
    {
        var jwtKey = configuration["JWT_SETTINGS_KEY"]
                  ?? configuration["JwtSettings:Key"]
                  ?? Environment.GetEnvironmentVariable("JWT_SETTINGS_KEY");

        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        bool isDevelopment = string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
        {
            if (isDevelopment)
            {
                jwtKey = "POS_System_Default_Development_Secret_Key_At_Least_32_Chars!";
            }
            else
            {
                throw new InvalidOperationException("CRITICAL: JWT Secret Key (JWT_SETTINGS_KEY or JwtSettings:Key) must be configured in production and must be at least 32 characters long.");
            }
        }
        else if (!isDevelopment && (jwtKey.Contains("Default_Development_Secret_Key") || jwtKey.Equals("ddf95c83c01224202681eee4525087512ece338e47f4c4897b6c5d72459b8795", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("CRITICAL: Default or historically leaked development JWT secret cannot be used in production. A secure random key must be generated.");
        }

        _securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        _issuer = configuration["JwtSettings:Issuer"] ?? "SolucionesPos";
        _audience = configuration["JwtSettings:Audience"] ?? "PosClient";
        var expiryMinutesStr = configuration["JwtSettings:ExpiryMinutes"] ?? "120";
        if (!int.TryParse(expiryMinutesStr, out var expiryMinutes) || expiryMinutes <= 0)
        {
            expiryMinutes = 120;
        }
        _expiryMinutes = expiryMinutes;
    }

    public string GenerateToken(User user, string scope = "pos:desktop")
    {
        var credentials = new SigningCredentials(_securityKey, SecurityAlgorithms.HmacSha256);

        var displayName = string.IsNullOrWhiteSpace(user.Name) ? user.FullName : user.Name;

        if (string.IsNullOrWhiteSpace(user.SecurityStamp))
        {
            user.SecurityStamp = Guid.NewGuid().ToString("N");
        }

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.Name, displayName),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim(ClaimTypes.SerialNumber, user.Cedula ?? string.Empty),
            new Claim("scope", scope),
            new Claim("security_stamp", user.SecurityStamp),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (user.MustChangePassword)
        {
            claims.Add(new Claim("must_change_password", "true"));
        }

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(_expiryMinutes),
            Issuer = _issuer,
            Audience = _audience,
            SigningCredentials = credentials
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }
}