using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Core.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Backend.API.Services;

/// <summary>
/// 8.150 (T3, design D3): mini-JWT HMAC para autorizaciones remotas. Reutiliza la misma clave de
/// firma que <see cref="TokenService"/> y fija la audiencia <c>pos:authorization</c>, de modo que
/// un token de autorizacion jamas sea aceptado por la API normal y viceversa.
/// </summary>
public class AuthorizationTokenService : IAuthorizationTokenService
{
    public const string AuthorizationAudience = "pos:authorization";

    private readonly SymmetricSecurityKey _securityKey;
    private readonly string _issuer;
    private readonly TimeSpan _tokenTtl;

    public AuthorizationTokenService(IConfiguration configuration, TimeSpan? tokenTtl = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

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

        _tokenTtl = tokenTtl ?? TimeSpan.FromSeconds(60);
        if (_tokenTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenTtl), "El TTL del token debe ser positivo.");
        }
    }

    public TimeSpan TokenTtl => _tokenTtl;

    public string Issue(
        int cashierUserId,
        int requestId,
        AuthorizationActionType actionType,
        int? saleId,
        string contextHash,
        DateTime resolvedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextHash);

        if (cashierUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cashierUserId), "El cajero debe ser un usuario valido.");
        }

        if (requestId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestId), "La solicitud debe tener un id valido.");
        }

        if (!Enum.IsDefined(actionType))
        {
            throw new ArgumentOutOfRangeException(nameof(actionType), "La accion de autorizacion no esta registrada.");
        }

        // ResolvedAt viaja desde snapshots EF (SQLite/Npgsql pueden devolverlo Unspecified):
        // se normaliza a UTC para que exp no dependa de la zona horaria del servidor.
        var resolvedAtUtc = resolvedAt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(resolvedAt, DateTimeKind.Utc)
            : resolvedAt.ToUniversalTime();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, cashierUserId.ToString(CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Jti, requestId.ToString(CultureInfo.InvariantCulture)),
            new(AuthorizationTokenClaimNames.ActionType, actionType.ToString()),
            new(AuthorizationTokenClaimNames.ContextHash, contextHash)
        };

        if (saleId.HasValue)
        {
            claims.Add(new Claim(AuthorizationTokenClaimNames.SaleId, saleId.Value.ToString(CultureInfo.InvariantCulture)));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            // nbf/iat anclados a la resolucion: la vigencia completa nace en ResolvedAt y el
            // handler no puede inyectar NotBefore = ahora (romperia tokens de resolucion pasada).
            NotBefore = resolvedAtUtc,
            IssuedAt = resolvedAtUtc,
            Expires = resolvedAtUtc.Add(_tokenTtl),
            Issuer = _issuer,
            Audience = AuthorizationAudience,
            SigningCredentials = new SigningCredentials(_securityKey, SecurityAlgorithms.HmacSha256)
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    public AuthorizationTokenClaims? Validate(string? rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        var handler = new JwtSecurityTokenHandler();
        try
        {
            var parameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _securityKey,
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = AuthorizationAudience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };

            handler.ValidateToken(rawToken, parameters, out var validatedToken);
            if (validatedToken is not JwtSecurityToken jwt)
            {
                return null;
            }

            var sub = jwt.Claims.FirstOrDefault(claim => claim.Type == JwtRegisteredClaimNames.Sub)?.Value;
            var jti = jwt.Claims.FirstOrDefault(claim => claim.Type == JwtRegisteredClaimNames.Jti)?.Value;
            var act = jwt.Claims.FirstOrDefault(claim => claim.Type == AuthorizationTokenClaimNames.ActionType)?.Value;
            var sal = jwt.Claims.FirstOrDefault(claim => claim.Type == AuthorizationTokenClaimNames.SaleId)?.Value;
            var ctx = jwt.Claims.FirstOrDefault(claim => claim.Type == AuthorizationTokenClaimNames.ContextHash)?.Value;

            if (!int.TryParse(sub, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cashierUserId) ||
                !int.TryParse(jti, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestId) ||
                !Enum.TryParse<AuthorizationActionType>(act, out var actionType) ||
                !Enum.IsDefined(actionType) ||
                string.IsNullOrWhiteSpace(ctx))
            {
                return null;
            }

            int? saleId = null;
            if (!string.IsNullOrWhiteSpace(sal))
            {
                if (!int.TryParse(sal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSaleId))
                {
                    return null;
                }

                saleId = parsedSaleId;
            }

            return new AuthorizationTokenClaims(cashierUserId, requestId, actionType, saleId, ctx, jwt.ValidTo);
        }
        catch (SecurityTokenException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
