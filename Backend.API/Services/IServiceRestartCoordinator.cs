namespace Backend.API.Services;

public interface IServiceRestartCoordinator
{
    bool TryScheduleRestart();
}