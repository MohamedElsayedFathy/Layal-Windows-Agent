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
    public static ReminderService ReminderService { get; set; } = null!;
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
}
