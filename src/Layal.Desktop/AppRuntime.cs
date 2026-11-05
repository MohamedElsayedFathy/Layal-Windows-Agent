using Layal.Models;
using Layal.Services;
using Layal.Views;

namespace Layal;

public static class AppRuntime
{
    public static string DataRoot { get; set; } = string.Empty;
    public static SettingsStore SettingsStore { get; set; } = null!;
    public static UserSettings Settings { get; set; } = new();
    public static DpapiSecretStore SecretStore { get; set; } = null!;
    public static SkillPackageService SkillPackageService { get; set; } = null!;
    public static MemoryService MemoryService { get; set; } = null!;
    public static TaskHistoryService TaskHistoryService { get; set; } = null!;
    /// <summary>Transient callback for a user-clicked reminder linked to a task; never persisted.</summary>
    public static Action<string>? OpenTaskNotification { get; set; }
    public static ReminderService ReminderService { get; set; } = null!;
    public static ReminderTaskSchedulerService? ReminderTaskSchedulerService { get; set; }
    public static SavedRoutineService SavedRoutineService { get; set; } = null!;
    public static GmailReadOnlyService GmailReadOnlyService { get; set; } = null!;
    public static ProtectedJsonStore<List<ChatMessage>> ChatHistoryStore { get; set; } = null!;
    public static ChatSessionService ChatSessionService { get; set; } = null!;
    public static AgentToolExecutor AgentToolExecutor { get; set; } = null!;
    public static ApiConnectionService ApiConnectionService { get; set; } = null!;
    public static BrowserAutomationService BrowserAutomationService { get; set; } = null!;
    public static AgentOrchestrator AgentOrchestrator { get; set; } = null!;
    public static string WorkspaceRoot { get; set; } = string.Empty;
    public static MainWindow? MainWindow { get; set; }
    public static FloatingMascotWindow? MascotWindow { get; set; }
    public static TrayService? Tray { get; set; }
    public static Action? ExitAction { get; set; }
    public static bool IsExiting { get; set; }
    /// <summary>
    /// Reconciles the single Windows dispatch task with both saved reminders and the optional
    /// local check-in. Check-ins are never scheduled while disabled or while the privacy shield
    /// or manual game mode is active.
    /// </summary>
    public static void SynchronizeLocalNotifications()
    {
        var scheduler = ReminderTaskSchedulerService;
        if (scheduler is null || ReminderService is null)
            return;

        var proactiveDueAt = ProactiveCheckInPolicy.GetNextDueAt(
            enabled: Settings.ProactiveCheckInEnabled,
            privacyShieldEnabled: Settings.PrivacyShieldEnabled,
            gameModeActive: IsGameModeActive,
            applicationExiting: IsExiting,
            lastNotifiedAt: Settings.LastProactiveCheckInAt,
            intervalMinutes: Settings.ProactiveCheckInIntervalMinutes);

        scheduler.Synchronize(ReminderService.GetAll(), proactiveDueAt);
    }

    /// <summary>
    /// True while the manual HUD is visible or an allowlisted foreground process is active.
    /// It suppresses proactive check-ins only; explicit user-created reminders remain scheduled.
    /// </summary>
    public static bool IsGameModeActive { get; set; }
}
