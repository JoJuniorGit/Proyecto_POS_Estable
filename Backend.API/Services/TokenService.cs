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
    private readonly IConfiguration _configuration;

    public TokenService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string GenerateToken(User user)
    {
        var jwtKey = _configuration["JWT_SETTINGS_KEY"] 
                  ?? _configuration["JwtSettings:Key"] 
                  ?? Environment.GetEnvironmentVariable("JWT_SETTINGS_KEY") 
                  ?? "POS_System_Default_Development_Secret_Key_At_Least_32_Chars!";
        var issuer = _configuration["JwtSettings:Issuer"] ?? "SolucionesPos";
        var audience = _configuration["JwtSettings:Audience"] ?? "PosClient";
        var expiryMinutesStr = _configuration["JwtSettings:ExpiryMinutes"] ?? "1440";
        if (!int.TryParse(expiryMinutesStr, out var expiryMinutes) || expiryMinutes <= 0)
        {
            expiryMinutes = 1440;
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var displayName = string.IsNullOrWhiteSpace(user.Name) ? user.FullName : user.Name;

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.Name, displayName),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim(ClaimTypes.SerialNumber, user.Cedula ?? string.Empty),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(expiryMinutes),
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = credentials
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }
}
