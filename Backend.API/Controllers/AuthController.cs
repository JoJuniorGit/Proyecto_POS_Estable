using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Core.DTOs;
using Core.Interfaces;
using Backend.API.Services;
using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

[EnableRateLimiting("AuthRateLimit")]
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly ITokenService _tokenService;
    private readonly ISecurityStampValidator? _stampValidator;

    public AuthController(
        IAuthService authService,
        ITokenService tokenService,
        ISecurityStampValidator? stampValidator = null)
    {
        _authService = authService;
        _tokenService = tokenService;
        _stampValidator = stampValidator;
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<LoginResultDto>> LoginAsync([FromBody] LoginRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Cedula))
        {
            return this.ApiBadRequest("El usuario es requerido.");
        }

        var authResult = await _authService.AuthenticateAsync(request.Cedula.Trim(), request.Password, cancellationToken);

        if (authResult.Outcome == AuthenticationOutcome.InvalidCredentials)
        {
            return this.ApiUnauthorized("Credenciales inválidas.");
        }

        if (authResult.Outcome == AuthenticationOutcome.PasswordChangeRequired)
        {
            return this.ApiPasswordChangeRequired("Debe cambiar su contraseña antes de continuar.");
        }

        // Token generation requires the tracked user entity (ITokenService contract); AuthService
        // cannot reference ITokenService because it lives in this API layer.
        var user = authResult.User!;

        var platform = request.Platform;
        if (string.IsNullOrWhiteSpace(platform) && Request?.Headers != null && Request.Headers.TryGetValue("X-Client-Platform", out var headerPlatform))
        {
            platform = headerPlatform.ToString();
        }

        bool isWeb = string.Equals(platform, "Web", StringComparison.OrdinalIgnoreCase);
        var scope = "pos:api";
        var token = _tokenService.GenerateToken(user, scope);

        if (isWeb && Response?.Cookies != null)
        {
            var cookieOptions = new Microsoft.AspNetCore.Http.CookieOptions
            {
                HttpOnly = true,
                Secure = Request?.IsHttps ?? false,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddMinutes(_tokenService.ExpiryMinutes)
            };
            Response.Cookies.Append("pos_jwt", token, cookieOptions);
        }

        return Ok(new LoginResultDto
        {
            User = ToUserDto(user),
            Token = isWeb ? null : token
        });
    }

    [NonAction]
    public Task<ActionResult<LoginResultDto>> Login(LoginRequest request) => LoginAsync(request);

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync(CancellationToken cancellationToken = default)
    {
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? User.FindFirst("sub")?.Value;
        if (int.TryParse(idClaim, out int uid))
        {
            if (_stampValidator != null)
            {
                await _stampValidator.RevokeUserStampAsync(uid);
            }
        }

        if (Response?.Cookies != null)
        {
            Response.Cookies.Delete("pos_jwt", new Microsoft.AspNetCore.Http.CookieOptions
            {
                HttpOnly = true,
                Secure = Request?.IsHttps ?? false,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
                Path = "/"
            });
        }
        return Ok(new MessageResponseDto("Sesión cerrada correctamente."));
    }

    [NonAction]
    public Task<IActionResult> Logout() => LogoutAsync();

    [AllowAnonymous]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePasswordAsync([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Cedula) ||
            string.IsNullOrWhiteSpace(request.CurrentPassword) ||
            string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return this.ApiBadRequest("Usuario, contraseña actual y nueva contraseña son requeridos.");
        }

        var result = await _authService.ChangePasswordAsync(request.Cedula.Trim(), request.CurrentPassword, request.NewPassword, cancellationToken);

        if (result.Outcome == PasswordChangeOutcome.InvalidCredentials)
        {
            return this.ApiUnauthorized("Credenciales inválidas o contraseña actual incorrecta.");
        }

        if (result.Outcome == PasswordChangeOutcome.PolicyViolation)
        {
            return this.ApiBadRequest(result.PolicyError);
        }

        _stampValidator?.InvalidateUserStamp(result.UserId);

        if (Response?.Cookies != null)
        {
            Response.Cookies.Delete("pos_jwt", new Microsoft.AspNetCore.Http.CookieOptions
            {
                HttpOnly = true,
                Secure = Request?.IsHttps ?? false,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
                Path = "/"
            });
        }

        return Ok(new MessageResponseDto("Contraseña actualizada correctamente. Inicie sesión con su nueva clave."));
    }

    [NonAction]
    public Task<IActionResult> ChangePassword(ChangePasswordRequest request) => ChangePasswordAsync(request);

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> GetMeAsync(CancellationToken cancellationToken = default)
    {
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                   ?? User.FindFirst("sub")?.Value;
        if (!int.TryParse(idClaim, out int currentUserId))
        {
            return this.ApiUnauthorized("No autorizado.");
        }

        var user = await _authService.GetCurrentUserAsync(currentUserId, cancellationToken);
        if (user == null)
        {
            return this.ApiUnauthorized("No autorizado.");
        }

        return Ok(user);
    }

    [NonAction]
    public Task<ActionResult<UserDto>> GetMe() => GetMeAsync();

    private static UserDto ToUserDto(Core.Entities.User user)
    {
        return new UserDto
        {
            Id = user.Id,
            Cedula = user.Cedula,
            Name = string.IsNullOrWhiteSpace(user.Name) ? user.FullName : user.Name,
            Role = user.Role,
            IsActive = user.IsActive
        };
    }
}
