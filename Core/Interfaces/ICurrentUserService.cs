using Core.Entities;

namespace Core.Interfaces;

public interface ICurrentUserService
{
    UserRole? UserRole { get; }
    string? UserId { get; }
    bool CanMutateCatalog { get; }
    bool CanMutateSettings { get; }
    bool CanMutateExchangeRate { get; }
}
