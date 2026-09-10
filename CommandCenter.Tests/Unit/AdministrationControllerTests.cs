using Backend.API.Controllers;
using Backend.API.Services;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class AdministrationControllerTests
{
    private static AdministrationController CreateController(
        Mock<IServiceRestartCoordinator>? coordinator = null,
        Mock<ICurrentUserService>? currentUser = null)
    {
        coordinator ??= new Mock<IServiceRestartCoordinator>();
        if (currentUser == null)
        {
            currentUser = new Mock<ICurrentUserService>();
            currentUser.Setup(u => u.CanMutateSettings).Returns(true);
        }
        return new AdministrationController(coordinator.Object, currentUser.Object);
    }

    [Fact]
    public void RestartSystem_WhenAdminAndCoordinatorAccepts_Returns202Accepted()
    {
        var coordinator = new Mock<IServiceRestartCoordinator>();
        coordinator.Setup(c => c.TryScheduleRestart()).Returns(true);
        var controller = CreateController(coordinator);

        var result = controller.RestartSystem();

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Equal(StatusCodes.Status202Accepted, accepted.StatusCode);
        coordinator.Verify(c => c.TryScheduleRestart(), Times.Once);
    }

    [Fact]
    public void RestartSystem_WhenCashier_Returns403ForbiddenProblemDetails()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(u => u.CanMutateSettings).Returns(false);
        var coordinator = new Mock<IServiceRestartCoordinator>();
        var controller = CreateController(coordinator, currentUser);

        var result = controller.RestartSystem();

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        Assert.NotNull(objectResult.Value);
        coordinator.Verify(c => c.TryScheduleRestart(), Times.Never);
    }

    [Fact]
    public void RestartSystem_WhenRestartAlreadyPending_Returns409Conflict()
    {
        var coordinator = new Mock<IServiceRestartCoordinator>();
        coordinator.Setup(c => c.TryScheduleRestart()).Returns(false);
        var controller = CreateController(coordinator);

        var result = controller.RestartSystem();

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public void ServiceRestartCoordinator_SchedulesOnlyOneRestartAtATime()
    {
        var coordinator = new NoOpRestartCoordinator();

        Assert.True(coordinator.TryScheduleRestart());
        Assert.False(coordinator.TryScheduleRestart());
    }

    private sealed class NoOpRestartCoordinator : ServiceRestartCoordinator
    {
        public NoOpRestartCoordinator() : base(Mock.Of<ILogger<ServiceRestartCoordinator>>())
        {
        }

        protected override void RunDeferredRestart()
        {
            // Sello de pruebas: no ejecutar sc.exe/nssm.exe contra el servicio real.
        }
    }
}