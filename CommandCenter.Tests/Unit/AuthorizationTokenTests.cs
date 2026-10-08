using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Text;
using Backend.API.Services;
using Core.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.150 (T3, design D3): token efimero de autorizacion (mini-JWT). Cubre emision con los
/// claims sub/jti/act/sal/ctx, vigencia anclada a ResolvedAt (nunca a "ahora"), firma, emisor
/// y la separacion estricta de audiencias con el token normal de la API (pos:authorization
/// vs PosClient).
/// </summary>
public class AuthorizationTokenTests
{
    private const string TestKey = "POS_Test_Super_Secret_Key_At_Least_32_Chars_Long!";
    private const string Issuer = "SolucionesPos";
    private const string ApiAudience = "PosClient";
    private static readonly string HashA = new('a', 64);

    private static IConfiguration CreateConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "JWT_SETTINGS_KEY", TestKey },
            { "JwtSettings:Issuer", Issuer },
            { "JwtSettings:Audience", ApiAudience },
            { "JwtSettings:ExpiryMinutes", "60" }
        })
        .Build();

    private static AuthorizationTokenService CreateService(int ttlSeconds = 60)
        => new(CreateConfiguration(), TimeSpan.FromSeconds(ttlSeconds));

    private static DateTime UtcNowSeconds()
    {
        var now = DateTime.UtcNow;
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
    }

    [Fact]
    public void Issue_ThenValidate_ReturnsAllClaimsAnchoredToResolvedAt()
    {
        var service = CreateService();
        var resolvedAt = UtcNowSeconds();

        var token = service.Issue(70, 5, AuthorizationActionType.ManualPriceOverride, 445, HashA, resolvedAt);

        var claims = service.Validate(token);

        Assert.NotNull(claims);
        Assert.Equal(70, claims!.CashierUserId);
        Assert.Equal(5, claims.RequestId);
        Assert.Equal(AuthorizationActionType.ManualPriceOverride, claims.ActionType);
        Assert.Equal(445, claims.SaleId);
        Assert.Equal(HashA, claims.ContextHash);
        Assert.Equal(resolvedAt.AddSeconds(60), claims.ExpiresAt);
        Assert.Equal(TimeSpan.FromSeconds(60), service.TokenTtl);
    }

    [Fact]
    public void Issue_EmitsExactClaimNamesAudienceAndIssuer()
    {
        var service = CreateService();
        var resolvedAt = UtcNowSeconds();

        var token = service.Issue(70, 5, AuthorizationActionType.ManualPriceOverride, 445, HashA, resolvedAt);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal(Issuer, jwt.Issuer);
        Assert.Contains(AuthorizationTokenService.AuthorizationAudience, jwt.Audiences);
        Assert.Equal("70", jwt.Claims.Single(claim => claim.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal("5", jwt.Claims.Single(claim => claim.Type == JwtRegisteredClaimNames.Jti).Value);
        Assert.Equal("ManualPriceOverride", jwt.Claims.Single(claim => claim.Type == "act").Value);
        Assert.Equal("445", jwt.Claims.Single(claim => claim.Type == "sal").Value);
        Assert.Equal(HashA, jwt.Claims.Single(claim => claim.Type == "ctx").Value);
        Assert.Equal(resolvedAt.AddSeconds(60), jwt.ValidTo);
    }

    [Fact]
    public void Validate_ExpiredToken_ReturnsNull()
    {
        var service = CreateService();
        var resolvedAt = UtcNowSeconds().AddSeconds(-120);

        var token = service.Issue(70, 5, AuthorizationActionType.ManualPriceOverride, 445, HashA, resolvedAt);

        Assert.Null(service.Validate(token));
    }

    [Fact]
    public void Validate_ExpIsAnchoredToResolvedAt_NotToIssueTime()
    {
        var service = CreateService();
        var resolvedAt = UtcNowSeconds().AddSeconds(-61);

        var token = service.Issue(70, 5, AuthorizationActionType.ManualPriceOverride, 445, HashA, resolvedAt);

        Assert.Null(service.Validate(token));
    }

    [Fact]
    public void Issue_UsesInjectedTtl()
    {
        var service = CreateService(ttlSeconds: 5);

        var expired = service.Issue(70, 5, AuthorizationActionType.ManualPriceOverride, 445, HashA, UtcNowSeconds().AddSeconds(-10));
        var valid = service.Issue(70, 5, AuthorizationActionType.ManualPriceOverride, 445, HashA, UtcNowSeconds().AddSeconds(-2));

        Assert.Null(service.Validate(expired));
        Assert.NotNull(service.Validate(valid));
    }

    [Fact]
    public void Validate_TamperedSignature_ReturnsNull()
    {
        var service = CreateService();
        var token = service.Issue(70, 5, AuthorizationActionType.ManualPriceOverride, 445, HashA, UtcNowSeconds());

        var parts = token.Split('.');
        var signature = parts[2].ToCharArray();
        var middle = signature.Length / 2;
        signature[middle] = signature[middle] == 'A' ? 'B' : 'A';
        parts[2] = new string(signature);

        Assert.Null(service.Validate(string.Join('.', parts)));
    }

    [Fact]
    public void Validate_NormalApiToken_IsRejectedByAuthorizationAudience()
    {
        var service = CreateService();
        var apiToken = new TokenService(CreateConfiguration()).GenerateToken(new User
        {
            Id = 1,
            Cedula = "V-00000001",
            Name = "Admin Uno",
            Role = UserRole.Admin
        });

        Assert.Null(service.Validate(apiToken));
    }

    [Fact]
    public void Validate_AuthorizationToken_IsRejectedByApiAudienceValidation()
    {
        var service = CreateService();
        var token = service.Issue(70, 5, AuthorizationActionType.ManualPriceOverride, 445, HashA, UtcNowSeconds());

        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestKey)),
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = ApiAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };

        Assert.ThrowsAny<SecurityTokenException>(() => new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _));
    }

    [Fact]
    public void Issue_WithoutSaleId_RoundTripsNullSaleId()
    {
        var service = CreateService();

        var token = service.Issue(70, 5, AuthorizationActionType.SaleCancellation, null, HashA, UtcNowSeconds());

        var claims = service.Validate(token);

        Assert.NotNull(claims);
        Assert.Null(claims!.SaleId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    public void Validate_InvalidInput_ReturnsNull(string? rawToken)
    {
        var service = CreateService();

        Assert.Null(service.Validate(rawToken));
    }
}
