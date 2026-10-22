using System.IO;
using System.Windows;
using Layal.Services;
using System.Collections.Generic;
using Layal.Views;

namespace Layal;

public partial class App : Application
{
    // Test harnesses can instantiate the real application resource dictionary without
    // launching a second tray agent/window set from the WPF dispatcher startup event.
    public static bool SuppressStartupForTests { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (SuppressStartupForTests) return;

        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Layal");
        Directory.CreateDirectory(dataRoot);

        AppRuntime.DataRoot = dataRoot;
        AppRuntime.SettingsStore = new SettingsStore(dataRoot);
        AppRuntime.Settings = AppRuntime.SettingsStore.Load();
        AppRuntime.SecretStore = new DpapiSecretStore(Path.Combine(dataRoot, "secrets", "ai-provider.dpapi"));
        AppRuntime.SkillPackageService = new SkillPackageService(Path.Combine(dataRoot, "Skills"));
        AppRuntime.MemoryService = new MemoryService(Path.Combine(dataRoot, "memory-notes.dpapi"));
        AppRuntime.TaskHistoryService = new TaskHistoryService(Path.Combine(dataRoot, "agent-tasks.dpapi"));
        AppRuntime.ChatHistoryStore = new ProtectedJsonStore<List<Layal.Models.ChatMessage>>(Path.Combine(dataRoot, "chat-history.dpapi"));
        AppRuntime.ChatSessionService = new ChatSessionService(
            Path.Combine(dataRoot, "chat-sessions.dpapi"),
            AppRuntime.ChatHistoryStore,
            AppRuntime.Settings.Language == "ar" ? "محادثة جديدة" : "New conversation");
        AppRuntime.WorkspaceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LayalWorkspace");
        AppRuntime.AgentToolExecutor = new AgentToolExecutor(AppRuntime.WorkspaceRoot, AppRuntime.MemoryService);
        AppRuntime.AgentOrchestrator = new AgentOrchestrator(new OpenAiCompatibleClient(), AppRuntime.AgentToolExecutor, AppRuntime.SkillPackageService, AppRuntime.TaskHistoryService);

        AppRuntime.ExitAction = ShutdownLayal;
        AppRuntime.MainWindow = new MainWindow();
        AppRuntime.MascotWindow = new FloatingMascotWindow();
        AppRuntime.Tray = new TrayService(
            showMain: () => Dispatcher.Invoke(() => AppRuntime.MainWindow?.ShowFromTray()),
            showMascot: () => Dispatcher.Invoke(() => AppRuntime.MascotWindow?.ShowAgain()),
            exit: () => Dispatcher.Invoke(ShutdownLayal),
            language: AppRuntime.Settings.Language);

        AppRuntime.MainWindow.Show();
        AppRuntime.MascotWindow.Show();
    }

    private void ShutdownLayal()
    {
        if (AppRuntime.IsExiting) return;
        AppRuntime.IsExiting = true;
        AppRuntime.Tray?.Dispose();
        // Close hidden windows too: a user may have hidden the main window before exiting from the tray.
        // MainWindow.Window_Closing cancels active requests when IsExiting is true.
        AppRuntime.MascotWindow?.Close();
        AppRuntime.MainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppRuntime.Tray?.Dispose();
        base.OnExit(e);
    }
}
