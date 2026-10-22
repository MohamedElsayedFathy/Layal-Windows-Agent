using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Layal.Models;
using Layal.Services;
using Layal.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

/// <summary>
/// Produces real WPF render snapshots for each main tab/theme/language on the Windows CI runner.
/// These are render smoke checks, not a substitute for testing physical monitor DPI and input.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MainWindowVisualSmokeTests
{
    [TestMethod]
    public void AllMainSectionsRenderInBothThemesAndLanguages()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RenderAllStates(); }
            catch (Exception exception) { failure = exception; }
            finally { App.SuppressStartupForTests = false; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromMinutes(3)))
            Assert.Fail("WPF visual smoke thread did not finish within three minutes.");
        if (failure is not null)
            Assert.Fail("WPF visual smoke failed: " + failure);
    }

    private static void RenderAllStates()
    {
        using var temp = new TestTempDirectory();
        App.SuppressStartupForTests = true;
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        InitializeRuntime(temp.Path);

        var outputRoot = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (string.IsNullOrWhiteSpace(outputRoot))
            outputRoot = Directory.GetCurrentDirectory();
        var outputDirectory = Path.Combine(outputRoot, "artifacts", "visual-smoke");
        Directory.CreateDirectory(outputDirectory);

        var window = new MainWindow
        {
            Width = 1320,
            Height = 860,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            ShowInTaskbar = false
        };
        AppRuntime.MainWindow = window;
        window.Show();
        window.UpdateLayout();

        var tabs = window.FindName("MainTabs") as TabControl
            ?? throw new InvalidOperationException("MainTabs was not found.");
        var applyTheme = typeof(MainWindow).GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyTheme was not found.");
        var applyLanguage = typeof(MainWindow).GetMethod("ApplyLanguage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyLanguage was not found.");

        var tabNames = new[] { "home", "chat", "tasks", "skills", "cameras", "memory", "settings" };
        var lines = new List<string>
        {
            "Layal Windows WPF visual smoke snapshots",
            $"Window DIPs: {window.Width:0}x{window.Height:0}",
            "Captures below are direct RenderTargetBitmap output at multiple render densities; physical monitor DPI switching remains a manual acceptance step."
        };

        foreach (var theme in new[] { "light", "dark" })
        foreach (var language in new[] { "ar", "en" })
        {
            AppRuntime.Settings.ThemeMode = theme;
            AppRuntime.Settings.Language = language;
            applyTheme.Invoke(window, null);
            applyLanguage.Invoke(window, null);

            for (var index = 0; index < Math.Min(tabNames.Length, tabs.Items.Count); index++)
            {
                tabs.SelectedIndex = index;
                tabs.UpdateLayout();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75 })
                {
                    var suffix = (int)Math.Round(scale * 100);
                    var path = Path.Combine(outputDirectory, $"app-{theme}-{language}-{tabNames[index]}-dpi{suffix}.png");
                    SaveSnapshot(window, path, scale);
                    lines.Add($"{Path.GetFileName(path)} | tab={tabNames[index]} | theme={theme} | language={language} | renderScale={scale:0.00} | dips={window.ActualWidth:0}x{window.ActualHeight:0}");
                }
            }
        }

        // Render the actual mascot Visual3D tree under representative task and social states.
        var mascot = new FloatingMascotWindow { ShowInTaskbar = false };
        AppRuntime.MascotWindow = mascot;
        mascot.Show();
        mascot.UpdateLayout();
        var animateTick = typeof(FloatingMascotWindow).GetMethod("AnimateMoodTick", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AnimateMoodTick was not found.");
        var mascotMoods = new[]
        {
            "idle", "curious", "thinking", "planning", "researching", "reading",
            "writing", "coding", "using_tool", "validating", "waiting",
            "happy", "love", "embarrassed", "frustrated", "celebrating", "error"
        };

        foreach (var mood in mascotMoods)
        {
            mascot.SetMood(mood, showReaction: false);
            for (var tick = 0; tick < 3; tick++) animateTick.Invoke(mascot, null);
            mascot.UpdateLayout();
            var path = Path.Combine(outputDirectory, $"mascot-{mood}.png");
            SaveSnapshot(mascot, path);
            lines.Add($"{Path.GetFileName(path)} | mood={mood} | size={mascot.ActualWidth:0}x{mascot.ActualHeight:0}");
        }

        File.WriteAllLines(Path.Combine(outputDirectory, "manifest.txt"), lines);
        AppRuntime.IsExiting = true;
        mascot.Close();
        window.Close();
        app.Shutdown();
        App.SuppressStartupForTests = false;
    }

    private static void InitializeRuntime(string root)
    {
        AppRuntime.SettingsStore = new SettingsStore(root);
        AppRuntime.Settings = new UserSettings();
        AppRuntime.SecretStore = new DpapiSecretStore(Path.Combine(root, "secrets", "visual-smoke.dpapi"));
        AppRuntime.SkillPackageService = new SkillPackageService(Path.Combine(root, "skills"));
        AppRuntime.MemoryService = new MemoryService(Path.Combine(root, "memory.dpapi"));
        AppRuntime.TaskHistoryService = new TaskHistoryService(Path.Combine(root, "tasks.dpapi"));
        AppRuntime.ChatHistoryStore = new ProtectedJsonStore<List<ChatMessage>>(Path.Combine(root, "chat.dpapi"));
        AppRuntime.ChatSessionService = new ChatSessionService(
            Path.Combine(root, "sessions.dpapi"), AppRuntime.ChatHistoryStore, "Visual smoke");
        AppRuntime.WorkspaceRoot = Path.Combine(root, "workspace");
        AppRuntime.AgentToolExecutor = new AgentToolExecutor(AppRuntime.WorkspaceRoot, AppRuntime.MemoryService);
        AppRuntime.AgentOrchestrator = new AgentOrchestrator(
            new OpenAiCompatibleClient(), AppRuntime.AgentToolExecutor,
            AppRuntime.SkillPackageService, AppRuntime.TaskHistoryService);
        AppRuntime.IsExiting = false;
    }

    private static void SaveSnapshot(FrameworkElement visual, string path, double renderScale = 1.0)
    {
        visual.UpdateLayout();
        renderScale = Math.Clamp(renderScale, 1.0, 2.0);
        var dpi = 96.0 * renderScale;
        var width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth * renderScale));
        var height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight * renderScale));
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        if (bitmap.PixelWidth < 1 || bitmap.PixelHeight < 1)
            throw new InvalidOperationException($"Empty snapshot generated for {visual.GetType().Name}.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
