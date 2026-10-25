using System.IO;
using System.Threading;
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

    private Mutex? _singleInstanceMutex;
    private bool _crashDialogShown;
    private string _logDirectory = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (SuppressStartupForTests) return;

        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Layal");
        Directory.CreateDirectory(dataRoot);
        _logDirectory = Path.Combine(dataRoot, "logs");
        RegisterGlobalExceptionLogging();

        // Two instances would show two tray icons/mascots and race on the same settings and encrypted stores.
        // A restart right after a kill/upgrade may briefly overlap with the old process, so wait before giving up.
        if (!TryAcquireSingleInstance())
        {
            Shutdown();
            return;
        }

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
        // Normalize empty history placeholders to the persisted UI language when an old history is reopened.
        AppRuntime.ChatSessionService.SetDefaultTitle(AppRuntime.Settings.Language == "ar" ? "محادثة جديدة" : "New conversation");
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

    private bool TryAcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(true, @"Local\Layal.Desktop.SingleInstance", out var createdNew);
            if (createdNew) return true;
            try { return _singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { return true; } // the previous instance died; we now own the mutex
        }
        catch
        {
            // Never block startup because the guard itself failed.
            return true;
        }
    }

    private void RegisterGlobalExceptionLogging()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("UI thread", args.Exception);
            // A failure before the main window exists cannot be recovered from; with
            // ShutdownMode=OnExplicitShutdown swallowing it would leave an invisible zombie process.
            if (AppRuntime.MainWindow is null) return;
            if (!_crashDialogShown)
            {
                _crashDialogShown = true; // avoid a dialog storm if a timer keeps throwing
                try
                {
                    MessageBox.Show(
                        "حدث خطأ غير متوقع وتم تسجيله. يمكنك متابعة العمل.\nAn unexpected error occurred and was logged. You can keep working.\n\n" + args.Exception.Message,
                        "Layal | ليال", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { /* a failing dialog must not escalate the original error */ }
            }
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("Unobserved task", args.Exception);
            args.SetObserved();
        };
    }

    private void WriteCrashLog(string source, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var path = Path.Combine(_logDirectory, "layal-errors.log");
            // Keep the log bounded: restart it once it grows past ~1 MB.
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                File.Move(path, path + ".old", overwrite: true);
            // Only exception type/stack are written; no settings, keys, prompts or file contents.
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {source}: {exception?.GetType().FullName}: {exception?.Message}{Environment.NewLine}{exception?.StackTrace}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
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
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* not owned (second instance) or already released */ }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
