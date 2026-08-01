namespace Sample.Domain;

public interface IFixtureReminderService;

public sealed class FixtureReminderService : IFixtureReminderService;

public sealed class FixtureMonitoringCoordinator
{
    private readonly IFixtureReminderService _reminderService;

    public FixtureMonitoringCoordinator(
        IFixtureReminderService reminderService)
    {
        _reminderService = reminderService;
    }
}
