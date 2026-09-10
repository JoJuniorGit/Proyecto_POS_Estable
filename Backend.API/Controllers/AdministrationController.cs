using Backend.API.Services;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/administration")]
public class AdministrationController : ControllerBase
{
    private readonly IServiceRestartCoordinator _restartCoordinator;
    private readonly ICurrentUserService _currentUserService;

    public AdministrationController(IServiceRestartCoordinator restartCoordinator, ICurrentUserService currentUserService)
    {
        ArgumentNullException.ThrowIfNull(restartCoordinator);
        ArgumentNullException.ThrowIfNull(currentUserService);
        _restartCoordinator = restartCoordinator;
        _currentUserService = currentUserService;
    }

    [HttpPost("restart")]
    [Authorize(Roles = "Admin")]
    public ActionResult RestartSystem()
    {
        if (!_currentUserService.CanMutateSettings)
        {
            return this.ApiForbidden("El rol Cajero no tiene permisos para reiniciar el sistema.");
        }

        if (!_restartCoordinator.TryScheduleRestart())
        {
            return this.ApiConflict(
                "Ya hay un reinicio del sistema en curso. Espere a que el servicio vuelva a responder.",
                "Se ignoró la petición para evitar reinicios simultáneos.");
        }

        return Accepted(new { Status = "restarting", Service = "PosBackendService" });
    }
}