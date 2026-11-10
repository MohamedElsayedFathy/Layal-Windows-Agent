using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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

    private static void AssertPrivacyShieldClearsUnsubmittedClipboardAttachments(MainWindow window)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var imageBytesField = typeof(MainWindow).GetField("_pendingVisionImageBytes", flags)
            ?? throw new InvalidOperationException("Pending image bytes field was not found.");
        var imageNameField = typeof(MainWindow).GetField("_pendingVisionFileName", flags)
            ?? throw new InvalidOperationException("Pending image filename field was not found.");
        var imageTypeField = typeof(MainWindow).GetField("_pendingVisionContentType", flags)
            ?? throw new InvalidOperationException("Pending image content-type field was not found.");
        var documentTextField = typeof(MainWindow).GetField("_pendingDocumentText", flags)
            ?? throw new InvalidOperationException("Pending document text field was not found.");
        var documentNameField = typeof(MainWindow).GetField("_pendingDocumentFileName", flags)
            ?? throw new InvalidOperationException("Pending document filename field was not found.");
        var documentNoticeField = typeof(MainWindow).GetField("_pendingDocumentNotice", flags)
            ?? throw new InvalidOperationException("Pending document notice field was not found.");
        var stopCaptures = typeof(MainWindow).GetMethod("StopCaptureSourcesForPrivacyShield", flags)
            ?? throw new InvalidOperationException("Privacy shield stop method was not found.");
        var screenCaptureField = typeof(MainWindow).GetField("_screenCaptureCancellation", flags)
            ?? throw new InvalidOperationException("Screen capture cancellation field was not found.");
        var clipboardReadField = typeof(MainWindow).GetField("_clipboardReadCancellation", flags)
            ?? throw new InvalidOperationException("Clipboard read cancellation field was not found.");
        var uiAutomationField = typeof(MainWindow).GetField("_hoveredElementInspectionCancellation", flags)
            ?? throw new InvalidOperationException("UI Automation cancellation field was not found.");
        var extractionField = typeof(MainWindow).GetField("_documentExtractionCancellation", flags)
            ?? throw new InvalidOperationException("Document extraction cancellation field was not found.");

        var previousScreenCapture = screenCaptureField.GetValue(window);
        var previousClipboardRead = clipboardReadField.GetValue(window);
        var previousUiAutomation = uiAutomationField.GetValue(window);
        var previousExtraction = extractionField.GetValue(window);
        if (previousScreenCapture is not null || previousClipboardRead is not null ||
            previousUiAutomation is not null || previousExtraction is not null)
            throw new InvalidOperationException("The privacy-shield regression test requires no active capture.");
        using var activeScreenCapture = new CancellationTokenSource();
        using var activeClipboardRead = new CancellationTokenSource();
        using var activeUiAutomation = new CancellationTokenSource();
        using var activeExtraction = new CancellationTokenSource();
        var previousShield = AppRuntime.Settings.PrivacyShieldEnabled;
        var previousImageBytes = imageBytesField.GetValue(window);
        var previousImageName = imageNameField.GetValue(window);
        var previousImageType = imageTypeField.GetValue(window);
        var previousDocumentText = documentTextField.GetValue(window);
        var previousDocumentName = documentNameField.GetValue(window);
        var previousDocumentNotice = documentNoticeField.GetValue(window);
        var clipboardImageBytes = new byte[] { 0xA1, 0xB2, 0xC3 };

        try
        {
            AppRuntime.Settings.PrivacyShieldEnabled = true;
            imageBytesField.SetValue(window, clipboardImageBytes);
            imageNameField.SetValue(window, "clipboard-image-20x20.png");
            imageTypeField.SetValue(window, "image/png");
            documentTextField.SetValue(window, "private clipboard text sentinel");
            documentNameField.SetValue(window, "Clipboard text");
            documentNoticeField.SetValue(window, "Local preview only.");
            screenCaptureField.SetValue(window, activeScreenCapture);
            clipboardReadField.SetValue(window, activeClipboardRead);
            uiAutomationField.SetValue(window, activeUiAutomation);
            extractionField.SetValue(window, activeExtraction);

            stopCaptures.Invoke(window, null);

            Assert.IsTrue(activeScreenCapture.IsCancellationRequested,
                "Enabling the privacy shield must cancel and close an in-flight screen capture preview.");
            Assert.IsTrue(activeClipboardRead.IsCancellationRequested,
                "Enabling the privacy shield must cancel an in-flight clipboard read before attachment.");
            Assert.IsTrue(activeUiAutomation.IsCancellationRequested,
                "Enabling the privacy shield must cancel an in-flight UI Automation inspection.");
            Assert.IsTrue(activeExtraction.IsCancellationRequested,
                "Enabling the privacy shield must cancel an in-flight document extraction before its preview can attach.");
            Assert.IsNull(imageBytesField.GetValue(window),
                "Enabling the privacy shield must discard an unsubmitted clipboard image.");
            Assert.IsTrue(clipboardImageBytes.All(value => value == 0),
                "The unsubmitted clipboard image byte buffer must be cleared.");
            Assert.IsNull(imageNameField.GetValue(window));
            Assert.IsNull(imageTypeField.GetValue(window));
            Assert.IsNull(documentTextField.GetValue(window),
                "Enabling the privacy shield must detach an unsubmitted clipboard text preview.");
            Assert.IsNull(documentNameField.GetValue(window));
            Assert.IsNull(documentNoticeField.GetValue(window));

            documentTextField.SetValue(window, "user-selected document text sentinel");
            documentNameField.SetValue(window, "selected-notes.txt");
            documentNoticeField.SetValue(window, "User selected local file.");
            stopCaptures.Invoke(window, null);

            Assert.AreEqual("user-selected document text sentinel", documentTextField.GetValue(window),
                "The privacy shield should not silently remove a file the user explicitly selected.");
            Assert.AreEqual("selected-notes.txt", documentNameField.GetValue(window));
        }
        finally
        {
            AppRuntime.Settings.PrivacyShieldEnabled = previousShield;
            screenCaptureField.SetValue(window, previousScreenCapture);
            clipboardReadField.SetValue(window, previousClipboardRead);
            uiAutomationField.SetValue(window, previousUiAutomation);
            extractionField.SetValue(window, previousExtraction);
            imageBytesField.SetValue(window, previousImageBytes);
            imageNameField.SetValue(window, previousImageName);
            imageTypeField.SetValue(window, previousImageType);
            documentTextField.SetValue(window, previousDocumentText);
            documentNameField.SetValue(window, previousDocumentName);
            documentNoticeField.SetValue(window, previousDocumentNotice);
            typeof(MainWindow).GetMethod("UpdateVisionAttachmentControls", flags)?.Invoke(window, null);
            typeof(MainWindow).GetMethod("UpdateDocumentAttachmentUi", flags)?.Invoke(window, null);
        }
    }

    private static void AssertPrivacyShieldRejectsLateCameraFrames(MainWindow window)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var analyze = typeof(MainWindow).GetMethod("AnalyzeVisionImageAsync", flags)
            ?? throw new InvalidOperationException("Vision analysis method was not found.");
        var previousShield = AppRuntime.Settings.PrivacyShieldEnabled;
        var lateFrame = new byte[] { 0x41, 0x59, 0x4F, 0x33, 0x33 };
        try
        {
            AppRuntime.Settings.PrivacyShieldEnabled = true;
            var pending = analyze.Invoke(window, new object[]
            {
                lateFrame, "image/jpeg", "camera snapshot", "late frame",
                false, false, false, true
            }) as Task ?? throw new InvalidOperationException("Vision analysis did not return a Task.");
            pending.GetAwaiter().GetResult();
            Assert.IsTrue(lateFrame.All(value => value == 0),
                "A late camera frame must be zeroed and rejected after enabling the privacy shield.");
        }
        finally
        {
            AppRuntime.Settings.PrivacyShieldEnabled = previousShield;
        }
    }
    private static void AssertAttachmentControlMutualExclusion(MainWindow window)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var imageBytesField = typeof(MainWindow).GetField("_pendingVisionImageBytes", flags)
            ?? throw new InvalidOperationException("Pending image bytes field was not found.");
        var imageTypeField = typeof(MainWindow).GetField("_pendingVisionContentType", flags)
            ?? throw new InvalidOperationException("Pending image content-type field was not found.");
        var imageNameField = typeof(MainWindow).GetField("_pendingVisionFileName", flags)
            ?? throw new InvalidOperationException("Pending image filename field was not found.");
        var documentTextField = typeof(MainWindow).GetField("_pendingDocumentText", flags)
            ?? throw new InvalidOperationException("Pending document text field was not found.");
        var documentNameField = typeof(MainWindow).GetField("_pendingDocumentFileName", flags)
            ?? throw new InvalidOperationException("Pending document filename field was not found.");
        var documentNoticeField = typeof(MainWindow).GetField("_pendingDocumentNotice", flags)
            ?? throw new InvalidOperationException("Pending document notice field was not found.");
        var updateVision = typeof(MainWindow).GetMethod("UpdateVisionAttachmentControls", flags)
            ?? throw new InvalidOperationException("Vision attachment UI updater was not found.");
        var updateDocument = typeof(MainWindow).GetMethod("UpdateDocumentAttachmentUi", flags)
            ?? throw new InvalidOperationException("Document attachment UI updater was not found.");

        var chooseImage = (Button?)window.FindName("ChooseImageButton")
            ?? throw new InvalidOperationException("ChooseImageButton was not found.");
        var chooseDocument = (Button?)window.FindName("ChooseDocumentButton")
            ?? throw new InvalidOperationException("ChooseDocumentButton was not found.");
        var captureScreen = (Button?)window.FindName("CaptureScreenRegionButton")
            ?? throw new InvalidOperationException("CaptureScreenRegionButton was not found.");
        var pasteClipboardImage = (Button?)window.FindName("PasteClipboardImageButton")
            ?? throw new InvalidOperationException("PasteClipboardImageButton was not found.");

        var previousImageBytes = imageBytesField.GetValue(window);
        var previousImageType = imageTypeField.GetValue(window);
        var previousImageName = imageNameField.GetValue(window);
        var previousDocumentText = documentTextField.GetValue(window);
        var previousDocumentName = documentNameField.GetValue(window);
        var previousDocumentNotice = documentNoticeField.GetValue(window);

        try
        {
            documentTextField.SetValue(window, null);
            documentNameField.SetValue(window, null);
            documentNoticeField.SetValue(window, null);
            imageBytesField.SetValue(window, new byte[] { 0x01, 0x02, 0x03 });
            imageTypeField.SetValue(window, "image/png");
            imageNameField.SetValue(window, "attachment-regression.png");
            updateVision.Invoke(window, null);

            Assert.IsFalse(chooseDocument.IsEnabled, "A pending image must disable choosing a text document.");
            Assert.IsFalse(pasteClipboardImage.IsEnabled, "A pending image must disable reading another clipboard image.");

            imageBytesField.SetValue(window, null);
            imageTypeField.SetValue(window, null);
            imageNameField.SetValue(window, null);
            documentTextField.SetValue(window, "document attachment sentinel");
            documentNameField.SetValue(window, "attachment-regression.txt");
            documentNoticeField.SetValue(window, "Local test preview only.");
            updateDocument.Invoke(window, null);

            Assert.IsFalse(chooseImage.IsEnabled, "A pending document must disable choosing another image.");
            Assert.IsFalse(captureScreen.IsEnabled, "A pending document must disable screen capture.");
            Assert.IsFalse(pasteClipboardImage.IsEnabled, "A pending document must disable clipboard image input.");
        }
        finally
        {
            imageBytesField.SetValue(window, previousImageBytes);
            imageTypeField.SetValue(window, previousImageType);
            imageNameField.SetValue(window, previousImageName);
            documentTextField.SetValue(window, previousDocumentText);
            documentNameField.SetValue(window, previousDocumentName);
            documentNoticeField.SetValue(window, previousDocumentNotice);
            updateVision.Invoke(window, null);
            updateDocument.Invoke(window, null);
        }
    }

    private static void AssertEscapeCancelsActiveChatAndAgentOperations(MainWindow window)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var chatField = typeof(MainWindow).GetField("_chatCancellation", flags)
            ?? throw new InvalidOperationException("Chat cancellation field was not found.");
        var taskField = typeof(MainWindow).GetField("_agentTaskCancellation", flags)
            ?? throw new InvalidOperationException("Agent task cancellation field was not found.");
        var speechStoppingField = typeof(MainWindow).GetField("_speechIsStopping", flags)
            ?? throw new InvalidOperationException("Speech stopping state was not found.");
        var discardSpeechField = typeof(MainWindow).GetField("_discardSpeechAudioOnStop", flags)
            ?? throw new InvalidOperationException("Pending speech discard state was not found.");
        var speechCancellationField = typeof(MainWindow).GetField("_speechCancellation", flags)
            ?? throw new InvalidOperationException("Speech cancellation field was not found.");
        var stopCapturesForPrivacy = typeof(MainWindow).GetMethod("StopCaptureSourcesForPrivacyShield", flags)
            ?? throw new InvalidOperationException("Privacy shield capture-stop method was not found.");
        var keyHandler = typeof(MainWindow).GetMethod("MainWindow_PreviewKeyDown", flags)
            ?? throw new InvalidOperationException("Main window keyboard handler was not found.");

        if (chatField.GetValue(window) is not null || taskField.GetValue(window) is not null ||
            speechCancellationField.GetValue(window) is not null || (bool)speechStoppingField.GetValue(window)!)
            throw new InvalidOperationException("The Escape regression test requires idle chat, task, and speech operations.");

        using var chatCancellation = new CancellationTokenSource();
        using var taskCancellation = new CancellationTokenSource();
        try
        {
            chatField.SetValue(window, chatCancellation);
            var chatEscape = CreateEscapeKeyEvent(window);
            keyHandler.Invoke(window, new object[] { window, chatEscape });
            Assert.IsTrue(chatCancellation.IsCancellationRequested, "Escape should cancel an active chat request.");
            Assert.IsTrue(chatEscape.Handled, "Escape should be handled when it cancels an operation.");

            chatField.SetValue(window, null);
            taskField.SetValue(window, taskCancellation);
            var taskEscape = CreateEscapeKeyEvent(window);
            keyHandler.Invoke(window, new object[] { window, taskEscape });
            Assert.IsTrue(taskCancellation.IsCancellationRequested, "Escape should cancel an active agent task.");
            Assert.IsTrue(taskEscape.Handled, "Escape should be handled when it cancels an agent task.");

            taskField.SetValue(window, null);
            speechStoppingField.SetValue(window, true);
            discardSpeechField.SetValue(window, false);
            var speechEscape = CreateEscapeKeyEvent(window);
            keyHandler.Invoke(window, new object[] { window, speechEscape });
            Assert.IsTrue((bool)discardSpeechField.GetValue(window)!,
                "Escape during StopRecordingAsync finalization must mark its audio for discard before transcription or upload.");
            Assert.IsTrue(speechEscape.Handled, "Escape should be handled while speech audio is being finalized.");

            discardSpeechField.SetValue(window, false);
            stopCapturesForPrivacy.Invoke(window, null);
            Assert.IsTrue((bool)discardSpeechField.GetValue(window)!,
                "Enabling the privacy shield during StopRecordingAsync finalization must mark its audio for discard.");
        }
        finally
        {
            chatField.SetValue(window, null);
            taskField.SetValue(window, null);
            speechStoppingField.SetValue(window, false);
            discardSpeechField.SetValue(window, false);
        }
    }

    private static KeyEventArgs CreateEscapeKeyEvent(MainWindow window)
    {
        var source = PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("The main window has no presentation source.");
        return new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
    }

    private static void AssertPrivateChatDoesNotPersistMessagesAndRestoresSavedConversation(MainWindow window, string dataRoot)
    {
        AppRuntime.ChatSessionService.SaveCurrentMessages(new[]
        {
            new ChatMessage { Role = "user", Content = "Saved conversation sentinel" },
            new ChatMessage { Role = "assistant", Content = "Saved reply sentinel" }
        });
        var savedConversationId = AppRuntime.ChatSessionService.GetCurrent().Id;
        var savedMemory = AppRuntime.MemoryService.Save(
            "Incognito memory guard sentinel",
            "Existing memory must remain unchanged while private chat is active.",
            category: "Regression test");
        var originalMemoryCount = AppRuntime.MemoryService.GetAll(includeArchived: true).Count;
        var originalTaskCount = AppRuntime.TaskHistoryService.GetRecent(100).Count;

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var enterPrivate = typeof(MainWindow).GetMethod("EnterPrivateChatMode", flags)
            ?? throw new InvalidOperationException("Private-chat entry method was not found.");
        var addMessage = typeof(MainWindow).GetMethod("AddMessage", flags)
            ?? throw new InvalidOperationException("Chat message method was not found.");
        var exitPrivate = typeof(MainWindow).GetMethod("ExitPrivateChatMode", flags)
            ?? throw new InvalidOperationException("Private-chat exit method was not found.");
        var privateModeField = typeof(MainWindow).GetField("_incognitoMode", flags)
            ?? throw new InvalidOperationException("Private-chat state field was not found.");
        var privateToggleHandler = typeof(MainWindow).GetMethod("IncognitoModeButton_Click", flags)
            ?? throw new InvalidOperationException("Private-chat toggle handler was not found.");
        var imageBytesField = typeof(MainWindow).GetField("_pendingVisionImageBytes", flags)
            ?? throw new InvalidOperationException("Pending image byte field was not found.");
        var imageTypeField = typeof(MainWindow).GetField("_pendingVisionContentType", flags)
            ?? throw new InvalidOperationException("Pending image content-type field was not found.");
        var imageNameField = typeof(MainWindow).GetField("_pendingVisionFileName", flags)
            ?? throw new InvalidOperationException("Pending image filename field was not found.");
        var documentTextField = typeof(MainWindow).GetField("_pendingDocumentText", flags)
            ?? throw new InvalidOperationException("Pending document text field was not found.");
        var documentNameField = typeof(MainWindow).GetField("_pendingDocumentFileName", flags)
            ?? throw new InvalidOperationException("Pending document filename field was not found.");
        var documentNoticeField = typeof(MainWindow).GetField("_pendingDocumentNotice", flags)
            ?? throw new InvalidOperationException("Pending document notice field was not found.");

        var imageBytes = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        imageBytesField.SetValue(window, imageBytes);
        imageTypeField.SetValue(window, "image/png");
        imageNameField.SetValue(window, "clipboard-image-private-test.png");
        documentTextField.SetValue(window, "temporary private document text");
        documentNameField.SetValue(window, "Clipboard text");
        documentNoticeField.SetValue(window, "Private local test preview.");

        var previousClipboardText = Clipboard.ContainsText()
            ? Clipboard.GetText()
            : null;
        try
        {
            enterPrivate.Invoke(window, null);
            Assert.IsTrue((bool)privateModeField.GetValue(window)!);
            Assert.IsTrue(AppRuntime.ChatSessionService.IsTemporarySession,
                "The chat store must enter reversible temporary mode before private messages are accepted.");
            Assert.IsTrue(AppRuntime.IsPrivateChatActive);
            var reminderTimerField = typeof(MainWindow).GetField("_reminderTimer", flags)
                ?? throw new InvalidOperationException("Reminder timer was not found.");
            var proactiveTimerField = typeof(MainWindow).GetField("_proactiveCheckInTimer", flags)
                ?? throw new InvalidOperationException("Proactive check-in timer was not found.");
            var reminderTimerWasEnabled = ((DispatcherTimer)reminderTimerField.GetValue(window)!).IsEnabled;
            var proactiveTimerWasEnabled = ((DispatcherTimer)proactiveTimerField.GetValue(window)!).IsEnabled;
            Assert.IsFalse(((DispatcherTimer)reminderTimerField.GetValue(window)!).IsEnabled,
                "Saved reminder delivery must pause during private chat.");
            Assert.IsFalse(((DispatcherTimer)proactiveTimerField.GetValue(window)!).IsEnabled,
                "Task-history based proactive check-ins must pause during private chat.");
            Assert.AreEqual(0, window.ChatMessages.Count, "Private chat must start without the previous transcript.");
            Assert.AreEqual(0, window.ChatConversations.Count, "Saved conversations must be hidden in private chat.");
            Assert.IsTrue(imageBytes.All(value => value == 0),
                "Image bytes waiting as an attachment must be cleared when private mode starts.");
            Assert.IsNull(imageBytesField.GetValue(window));
            Assert.IsNull(imageTypeField.GetValue(window));
            Assert.IsNull(imageNameField.GetValue(window));
            Assert.IsNull(documentTextField.GetValue(window));
            Assert.IsNull(documentNameField.GetValue(window));
            Assert.IsNull(documentNoticeField.GetValue(window));

            var historySelector = (ComboBox?)window.FindName("ChatHistorySelector")
                ?? throw new InvalidOperationException("Chat history selector was not found.");
            var tasksTab = (TabItem?)window.FindName("TasksTab")
                ?? throw new InvalidOperationException("TasksTab was not found.");
            var memoryTab = (TabItem?)window.FindName("MemoryTab")
                ?? throw new InvalidOperationException("MemoryTab was not found.");
            Assert.IsFalse(historySelector.IsEnabled, "Saved history must not be selectable in private chat.");
            Assert.IsFalse(tasksTab.IsEnabled, "The entire task workspace must be unavailable to UI Automation.");
            Assert.IsFalse(memoryTab.IsEnabled, "The entire memory workspace must be unavailable to UI Automation.");

            foreach (var name in new[]
            {
                "ManageRemindersButton", "ManageRoutinesButton", "WorkspaceHubButton",
                "RunAgentTaskButton", "TaskOpenNotepadButton", "RecentTasksList", "TaskEventsList",
                "TaskProgressStatus", "TaskProgressBar", "GameModeButton", "GameModeSettingsButton",
                "CopyTaskLogButton", "CopyTaskResultButton", "SaveMemoryButton", "NewMemoryButton",
                "MemorySearchButton", "MemoryList", "MemoryTitleInput", "MemoryContentInput",
                "MemoryResult", "MemoryEmptyStateText", "ImportMemoryButton", "ExportMemoryButton",
                "LoreWebButton", "ProactiveCheckInEnabledCheckBox", "ProactiveCheckInIntervalSelector"
            })
            {
                var control = window.FindName(name) as UIElement
                    ?? throw new InvalidOperationException($"{name} was not found.");
                Assert.IsFalse(control.IsEnabled,
                    $"{name} must not expose an enabled UI Automation action during private chat.");
            }

            addMessage.Invoke(window, new object[] { "user", "Private-only sentinel" });
            Assert.AreEqual(1, window.ChatMessages.Count);
            AppRuntime.ChatSessionService.SaveCurrentMessages(new[]
            {
                new ChatMessage { Role = "user", Content = "Direct service bypass must stay temporary" }
            });

            // Re-open the encrypted file through a fresh service, as a new process would.
            var reopenedDuringPrivateChat = new ChatSessionService(
                Path.Combine(dataRoot, "sessions.dpapi"),
                new ProtectedJsonStore<List<ChatMessage>>(Path.Combine(dataRoot, "chat.dpapi")),
                "Visual smoke");
            Assert.IsFalse(reopenedDuringPrivateChat.IsTemporarySession);
            Assert.AreEqual(1, reopenedDuringPrivateChat.GetAll().Count);
            Assert.AreEqual(savedConversationId, reopenedDuringPrivateChat.GetCurrent().Id);
            Assert.AreEqual(2, reopenedDuringPrivateChat.GetCurrent().Messages.Count);
            Assert.AreEqual("Saved conversation sentinel", reopenedDuringPrivateChat.GetCurrent().Messages[0].Content);
            Assert.AreEqual("Saved reply sentinel", reopenedDuringPrivateChat.GetCurrent().Messages[1].Content);
            Assert.IsFalse(reopenedDuringPrivateChat.GetCurrent().Messages.Any(message =>
                message.Content.Contains("Private", StringComparison.OrdinalIgnoreCase)),
                "A newly constructed service must never load Incognito messages from disk.");

            // Exercise UI handlers directly, bypassing tab navigation as a UIA/programmatic caller could.
            void InvokeGuardedHandler(string methodName)
            {
                var handler = typeof(MainWindow).GetMethod(methodName, flags)
                    ?? throw new InvalidOperationException($"Private-mode guard handler {methodName} was not found.");
                handler.Invoke(window, new object[] { window, new RoutedEventArgs() });
            }

            foreach (var methodName in new[]
            {
                "ManageRemindersButton_Click",
                "ManageRoutinesButton_Click",
                "WorkspaceHubButton_Click",
                "RunAgentTaskButton_Click",
                "MemorySearchButton_Click",
                "NewMemoryButton_Click",
                "SaveMemoryButton_Click",
                "ArchiveMemoryButton_Click",
                "DeleteMemoryButton_Click",
                "ImportMemoryButton_Click",
                "ExportMemoryButton_Click",
                "LoreWebButton_Click"
            })
                InvokeGuardedHandler(methodName);

            var taskResult = (TextBox?)window.FindName("TaskResult")
                ?? throw new InvalidOperationException("TaskResult was not found.");
            taskResult.Text = "Private task history sentinel";
            const string clipboardSentinel = "Clipboard must not be overwritten by a private task-copy request.";
            Clipboard.SetText(clipboardSentinel);
            InvokeGuardedHandler("CopyTaskResultButton_Click");
            Assert.AreEqual(clipboardSentinel, Clipboard.GetText(),
                "A task-copy handler must not export task history from the private workspace.");

            Assert.AreEqual(originalMemoryCount, AppRuntime.MemoryService.GetAll(includeArchived: true).Count);
            Assert.AreEqual(savedMemory.Content,
                AppRuntime.MemoryService.GetAll(includeArchived: true)
                    .Single(note => note.Id == savedMemory.Id).Content,
                "Existing encrypted memory must remain unchanged while private mode is active.");
            Assert.AreEqual(originalTaskCount, AppRuntime.TaskHistoryService.GetRecent(100).Count,
                "No task-history record may be created from blocked private workspace handlers.");

            var agentTaskField = typeof(MainWindow).GetField("_agentTaskCancellation", flags)
                ?? throw new InvalidOperationException("Agent-task cancellation field was not found.");
            using (var activeAgentTask = new CancellationTokenSource())
            {
                agentTaskField.SetValue(window, activeAgentTask);
                privateToggleHandler.Invoke(window, new object[] { window, new RoutedEventArgs() });
                Assert.IsTrue((bool)privateModeField.GetValue(window)!,
                    "Private chat must remain active until an agent task has stopped.");
                Assert.IsFalse(activeAgentTask.IsCancellationRequested,
                    "Attempting to exit private chat must not silently cancel or corrupt an active task.");
                Assert.AreEqual(1, window.ChatMessages.Count,
                    "A blocked exit must leave the unsaved private transcript intact.");
                agentTaskField.SetValue(window, null);
            }

            exitPrivate.Invoke(window, null);
            Assert.IsFalse((bool)privateModeField.GetValue(window)!);
            Assert.IsFalse(AppRuntime.ChatSessionService.IsTemporarySession,
                "Exiting private mode must restore normal chat persistence.");
            Assert.IsFalse(AppRuntime.IsPrivateChatActive,
                "The process-wide private flag must be cleared after exiting Incognito.");
            Assert.AreEqual(reminderTimerWasEnabled,
                ((DispatcherTimer)reminderTimerField.GetValue(window)!).IsEnabled,
                "Saved reminder delivery must return to its exact pre-Incognito state.");
            Assert.AreEqual(proactiveTimerWasEnabled && AppRuntime.Settings.ProactiveCheckInEnabled,
                ((DispatcherTimer)proactiveTimerField.GetValue(window)!).IsEnabled,
                "Proactive check-ins must return to their pre-Incognito state unless the feature is now disabled.");
            Assert.IsTrue(historySelector.IsEnabled, "Saved history selector should return after private chat.");
            var historyList = (ListBox?)window.FindName("ChatHistoryList")
                ?? throw new InvalidOperationException("ChatHistoryList was not found.");
            Assert.IsTrue(historyList.IsEnabled, "The visible saved-history list should return after private chat.");
            Assert.IsTrue(tasksTab.IsEnabled, "Task workspace should return after private chat.");
            Assert.IsTrue(memoryTab.IsEnabled, "Memory workspace should return after private chat.");
            Assert.AreEqual(2, window.ChatMessages.Count, "Exiting private chat should restore the saved transcript.");
            Assert.AreEqual("Saved conversation sentinel", window.ChatMessages[0].Content);
            Assert.AreEqual("Saved reply sentinel", window.ChatMessages[1].Content);
            Assert.AreEqual(1, window.ChatConversations.Count);
            Assert.AreEqual(2, AppRuntime.ChatSessionService.GetCurrent().Messages.Count);

            var reopenedAfterPrivateChat = new ChatSessionService(
                Path.Combine(dataRoot, "sessions.dpapi"),
                new ProtectedJsonStore<List<ChatMessage>>(Path.Combine(dataRoot, "chat.dpapi")),
                "Visual smoke");
            Assert.AreEqual(2, reopenedAfterPrivateChat.GetCurrent().Messages.Count);
            Assert.IsFalse(reopenedAfterPrivateChat.GetCurrent().Messages.Any(message =>
                message.Content.Contains("Private", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            if (previousClipboardText is null)
                Clipboard.Clear();
            else
                Clipboard.SetText(previousClipboardText);
        }
    }


    private static void RenderAllStates()
    {
        using var temp = new TestTempDirectory();
        App.SuppressStartupForTests = true;
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        InitializeRuntime(temp.Path);
        // Rendering tests must not request real device-location permission or network weather.
        AppRuntime.Settings.UseAutomaticLocation = false;

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
        foreach (var captionName in new[] { "MinimizeWindowButton", "MaximizeWindowButton", "CloseWindowButton" })
        {
            var captionButton = window.FindName(captionName) as Button
                ?? throw new InvalidOperationException($"{captionName} was not found.");
            Assert.AreEqual(44, captionButton.Width, 0.1,
                $"{captionName} must use the standardized 44-DIP width.");
            Assert.AreEqual(40, captionButton.Height, 0.1,
                $"{captionName} must meet the 40-DIP minimum control height.");
        }
        var closeHistoryButton = window.FindName("CloseChatHistoryButton") as Button
            ?? throw new InvalidOperationException("CloseChatHistoryButton was not found.");
        Assert.AreEqual(40, closeHistoryButton.Width, 0.1,
            "The chat history close action must meet the standardized 40-DIP touch target.");
        Assert.AreEqual(40, closeHistoryButton.Height, 0.1,
            "The chat history close action must meet the standardized 40-DIP touch target.");
        AssertInitialWindowFitsWorkArea(window);
        AssertClockWeatherDashboard(window);
        var windowContent = window.Content as FrameworkElement
            ?? throw new InvalidOperationException("MainWindow content root was not found.");
        var rootGrid = window.FindName("WindowRoot") as Grid
            ?? throw new InvalidOperationException("WindowRoot was not found.");
        AssertOpaqueWindowShell(window, rootGrid, "initial window state");

        var tabs = window.FindName("MainTabs") as TabControl
            ?? throw new InvalidOperationException("MainTabs was not found.");
        window.UpdateLayout();
        AssertNavigationRailDoesNotOverlapContent(window, "expanded initial layout");
        Assert.AreSame(window.FindName("ChatTab"), tabs.SelectedItem,
            "The primary window must open directly into the glass chat view, not the legacy home dashboard.");
        foreach (var visualSurface in new[] { "AmbientGlassLayer", "ChatMessagesShell", "ChatStatusShell", "ChatComposerShell", "TopBarBrandRow", "BottomDockShell", "DockNavigationItems", "ChatHistoryPanel", "ChatHistoryList" })
            Assert.IsNotNull(window.FindName(visualSurface),
                $"The updated layered-glass chat surface '{visualSurface}' must be present in the shipped UI.");
        var applyTheme = typeof(MainWindow).GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyTheme was not found.");
        var applyLanguage = typeof(MainWindow).GetMethod("ApplyLanguage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyLanguage was not found.");

        var tabNames = new[] { "overview", "chat", "tasks", "skills", "cameras", "memory", "api", "settings" };
        Assert.AreEqual(tabNames.Length, tabs.Items.Count,
            "The visual audit must render every expected main section; the test must not silently skip a missing tab.");

        var expectedTabs = new[]
        {
            window.FindName("HomeTab") as TabItem,
            window.FindName("ChatTab") as TabItem,
            window.FindName("TasksTab") as TabItem,
            window.FindName("SkillsTab") as TabItem,
            window.FindName("CamerasTab") as TabItem,
            window.FindName("MemoryTab") as TabItem,
            window.FindName("ApiIntegrationsTab") as TabItem,
            window.FindName("SettingsTab") as TabItem
        };
        Assert.AreEqual(tabNames.Length, expectedTabs.Length);
        for (var i = 0; i < expectedTabs.Length; i++)
        {
            Assert.IsNotNull(expectedTabs[i], $"Expected named tab '{tabNames[i]}' was not found.");
            Assert.AreSame(expectedTabs[i], tabs.Items[i], $"Tab order mismatch at '{tabNames[i]}'.");
        }
        AssertAccessibleStatusRegions(window);

        var historySelector = window.FindName("ChatHistorySelector") as ComboBox
            ?? throw new InvalidOperationException("ChatHistorySelector was not found.");
        AssertThemeTogglePersistsLivePalette(window, historySelector);
        var recentTasksGroup = window.FindName("RecentTasksGroup") as GroupBox
            ?? throw new InvalidOperationException("RecentTasksGroup was not found.");
        var languageButton = window.FindName("LanguageButton") as Button
            ?? throw new InvalidOperationException("LanguageButton was not found.");
        var titleText = window.FindName("TitleText") as TextBlock
            ?? throw new InvalidOperationException("TitleText was not found.");
        var copyChatButton = window.FindName("CopyChatButton") as Button
            ?? throw new InvalidOperationException("CopyChatButton was not found.");
        var performanceMetricsButton = window.FindName("PerformanceMetricsButton") as Button
            ?? throw new InvalidOperationException("PerformanceMetricsButton was not found.");
        var performanceMetricsStatus = window.FindName("PerformanceMetricsStatus") as TextBlock
            ?? throw new InvalidOperationException("PerformanceMetricsStatus was not found.");
        Assert.IsTrue(performanceMetricsButton.IsEnabled,
            "The local performance sampler must be available without a network/provider connection.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(performanceMetricsButton)),
            "The performance sampler must explain its local-only scope and GPU limitation.");
        Assert.IsTrue(AutomationProperties.GetHelpText(performanceMetricsButton)
                .Contains("GPU", StringComparison.OrdinalIgnoreCase),
            "The UI must not imply that the current-process sampler measures GPU.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(performanceMetricsStatus)),
            "The result area must expose a screen-reader name.");
        var newConversationButton = window.FindName("NewConversationButton") as Button
            ?? throw new InvalidOperationException("NewConversationButton was not found.");
        var clearHistoryButton = window.FindName("ClearSavedChatHistoryButton") as Button
            ?? throw new InvalidOperationException("ClearSavedChatHistoryButton was not found.");
        var sendButton = window.FindName("SendButton") as Button
            ?? throw new InvalidOperationException("SendButton was not found.");
        var runTaskButton = window.FindName("RunAgentTaskButton") as Button
            ?? throw new InvalidOperationException("RunAgentTaskButton was not found.");
        var baseUrlInput = window.FindName("BaseUrlInput") as TextBox
            ?? throw new InvalidOperationException("BaseUrlInput was not found.");
        var modelInput = window.FindName("ModelInput") as TextBox
            ?? throw new InvalidOperationException("ModelInput was not found.");
        var apiKeyInput = window.FindName("ApiKeyInput") as PasswordBox
            ?? throw new InvalidOperationException("ApiKeyInput was not found.");
        var githubSkillUrlInput = window.FindName("GitHubSkillUrlInput") as TextBox
            ?? throw new InvalidOperationException("GitHubSkillUrlInput was not found.");

        AssertKeyboardAccessibility(window, sendButton, window.FindName("ChatInput") as TextBox
            ?? throw new InvalidOperationException("ChatInput was not found."));
        AssertChatMessageDirectionContracts(window, applyLanguage);

        var lines = new List<string>
        {
            "Layal Windows WPF visual smoke snapshots",
            $"Window Width/Height DIPs after show: {window.Width:0}x{window.Height:0}",
            $"Actual client content DIPs: {windowContent.ActualWidth:0}x{windowContent.ActualHeight:0}",
            "Captures render the full WPF Window (including its own theme background) at 96 DPI multiplied by renderScale; this does not emulate physical monitor DPI or multi-monitor movement.",
            "Coverage: 128 full-window images (8 sections x 2 themes x 2 languages x 4 render scales), plus 32 compact-window images (8 sections x 2 themes x 2 languages at 900x560 DIPs).",
            "Compact captures test window resizing only; they do not certify OS display scaling at 100%, 125%, 150%, or 175%."
        };

        foreach (var theme in new[] { "light", "dark" })
        foreach (var language in new[] { "ar", "en" })
        {
            AppRuntime.Settings.ThemeMode = theme;
            AppRuntime.Settings.Language = language;
            applyTheme.Invoke(window, null);
            applyLanguage.Invoke(window, null);
            Assert.AreEqual(language == "ar" ? "قياس موارد ليال الآن" : "Measure Layal resources",
                performanceMetricsButton.Content as string);
            Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(performanceMetricsButton)));
            window.UpdateLayout();
            AssertLanguageLayoutContracts(window, language);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            AssertOpaqueWindowShell(window, rootGrid, $"{theme}/{language}");
            AssertEmptyStateLocalizationAndVisibility(window, language);
            Assert.AreEqual(FlowDirection.LeftToRight, window.FlowDirection,
                "The root window must not horizontally mirror icons or glyphs in RTL mode.");
            AssertThemeContrastAndControlSurfaces(theme, language, historySelector);
            if (language == "ar")
            {
                Assert.AreEqual("نسخ الشات", BidiTextNormalizer.StripDirectionalControls(copyChatButton.Content as string));
                Assert.AreEqual("محادثة جديدة", BidiTextNormalizer.StripDirectionalControls(newConversationButton.Content as string));
                Assert.AreEqual("مسح السجل المحفوظ", BidiTextNormalizer.StripDirectionalControls(clearHistoryButton.Content as string));
                Assert.AreEqual("إرسال", BidiTextNormalizer.StripDirectionalControls(sendButton.Content as string));
                Assert.AreEqual("تنفيذ المهمة", BidiTextNormalizer.StripDirectionalControls(runTaskButton.Content as string));

                Assert.IsTrue((languageButton.Content as string)?.Contains("\u2066English\u2069", StringComparison.Ordinal) == true,
                    "English language-toggle text must retain left-to-right order on the Arabic screen.");
                Assert.IsTrue(titleText.Text.Contains("\u2066Layal\u2069", StringComparison.Ordinal),
                    "Latin text in the Arabic title must retain left-to-right order.");
                Assert.AreEqual(FlowDirection.LeftToRight, titleText.FlowDirection,
                    "Arabic text should be right-aligned locally rather than mirroring its parent visual.");
                Assert.AreEqual(TextAlignment.Right, titleText.TextAlignment,
                    "Arabic title text should align to the right without flipping icons or glyph shapes.");
                Assert.AreEqual(FlowDirection.LeftToRight, baseUrlInput.FlowDirection,
                    "API base URLs must stay left-to-right on the Arabic screen.");
                Assert.AreEqual(FlowDirection.LeftToRight, modelInput.FlowDirection,
                    "Model IDs must stay left-to-right on the Arabic screen.");
                Assert.AreEqual(FlowDirection.LeftToRight, apiKeyInput.FlowDirection,
                    "API keys must stay left-to-right on the Arabic screen.");
                Assert.AreEqual(FlowDirection.LeftToRight, githubSkillUrlInput.FlowDirection,
                    "GitHub URLs must stay left-to-right on the Arabic screen.");
            }
            else
            {
                Assert.AreEqual("Copy chat", BidiTextNormalizer.StripDirectionalControls(copyChatButton.Content as string));
                Assert.AreEqual("New conversation", BidiTextNormalizer.StripDirectionalControls(newConversationButton.Content as string));
                Assert.AreEqual("Clear saved history", BidiTextNormalizer.StripDirectionalControls(clearHistoryButton.Content as string));
                Assert.AreEqual("Send", BidiTextNormalizer.StripDirectionalControls(sendButton.Content as string));
                Assert.AreEqual("Run task", BidiTextNormalizer.StripDirectionalControls(runTaskButton.Content as string));
            }

            for (var index = 0; index < tabNames.Length; index++)
            {
                tabs.SelectedIndex = index;
                tabs.UpdateLayout();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Keyboard.ClearFocus();

                var expectedCurrentSection = tabNames[index] switch
                {
                    "overview" => language == "ar" ? "نظرة عامة" : "Overview",
                    "chat" => language == "ar" ? "المحادثة / الرئيسية" : "Home (Chat)",
                    "tasks" => language == "ar" ? "المهام" : "Tasks",
                    "skills" => language == "ar" ? "المهارات" : "Skills",
                    "cameras" => language == "ar" ? "الكاميرات" : "Cameras",
                    "memory" => language == "ar" ? "الذاكرة" : "Memory",
                    "api" => language == "ar" ? "تكاملات API" : "API integrations",
                    "settings" => language == "ar" ? "الإعدادات" : "Settings",
                    _ => throw new InvalidOperationException($"Unknown main section '{tabNames[index]}'.")
                };
                var navigationHelpText = AutomationProperties.GetHelpText(tabs);
                Assert.IsTrue(navigationHelpText.Contains(expectedCurrentSection, StringComparison.OrdinalIgnoreCase),
                    $"Main navigation UI Automation status must identify '{tabNames[index]}' after selection in {language}.");

                if (tabNames[index] == "chat")
                {
                    var expectedCopy = language == "ar" ? "نسخ الشات" : "Copy chat";
                    var expectedNew = language == "ar" ? "محادثة جديدة" : "New conversation";
                    var expectedClear = language == "ar" ? "مسح السجل المحفوظ" : "Clear saved history";
                    Assert.AreEqual(expectedCopy,
                        BidiTextNormalizer.StripDirectionalControls(FindVisualText(copyChatButton)),
                        $"Rendered copy button text is incorrect for {theme}/{language}.");
                    Assert.AreEqual(expectedNew,
                        BidiTextNormalizer.StripDirectionalControls(FindVisualText(newConversationButton)),
                        $"Rendered new-conversation button text is incorrect for {theme}/{language}.");
                    Assert.AreEqual(expectedClear,
                        BidiTextNormalizer.StripDirectionalControls(FindVisualText(clearHistoryButton)),
                        $"Rendered clear-history button text is incorrect for {theme}/{language}.");
                }

                var selectedTab = tabs.SelectedItem as TabItem
                    ?? throw new InvalidOperationException($"Selected tab '{tabNames[index]}' is not a TabItem.");
                var tabContent = selectedTab.Content as FrameworkElement
                    ?? throw new InvalidOperationException($"Content for tab '{tabNames[index]}' was not found.");
                Assert.IsTrue(tabContent.ActualWidth > 0 && tabContent.ActualHeight > 0,
                    $"Tab '{tabNames[index]}' has an empty viewport for theme={theme}, language={language}.");
                if (tabNames[index] == "overview")
                {
                    var metricsGrid = window.FindName("HomeMetricsGrid") as UniformGrid
                        ?? throw new InvalidOperationException("HomeMetricsGrid was not found.");
                    var expectedColumns = metricsGrid.ActualWidth <= 880 ? 2 : 4;
                    Assert.AreEqual(expectedColumns, metricsGrid.Columns,
                        $"The overview must follow its actual available width. Width={metricsGrid.ActualWidth:0.0} DIP, theme={theme}, language={language}.");
                }
                if (tabNames[index] == "chat")
                {
                    var weatherCard = window.FindName("ClockWeatherCard") as Border
                        ?? throw new InvalidOperationException("ClockWeatherCard was not found.");
                    var subtitle = window.FindName("SubtitleText") as TextBlock
                        ?? throw new InvalidOperationException("SubtitleText was not found.");
                    Assert.AreEqual(Visibility.Visible, weatherCard.Visibility,
                        "The full-height chat layout must retain its clock and weather summary.");
                    Assert.AreEqual(Visibility.Visible, subtitle.Visibility,
                        "The full-width title bar must retain its subtitle.");
                    AssertPageHeroGradientTracksPalette($"{theme}/{language} full-size chat");
                }

                if (tabNames[index] == "tasks")
                {
                    var expectedBorder = Application.Current.TryFindResource("Layal.Border") as SolidColorBrush
                        ?? throw new InvalidOperationException("Layal.Border was not resolved.");
                    var actualBorder = recentTasksGroup.BorderBrush as SolidColorBrush
                        ?? throw new InvalidOperationException("Task GroupBox border is not a theme brush.");
                    Assert.AreEqual(expectedBorder.Color, actualBorder.Color,
                        $"Task GroupBox border does not follow the active theme ({theme}/{language}).");
                    Assert.IsNull(recentTasksGroup.FocusVisualStyle,
                        "Task GroupBox must not add a system focus outline over the themed border.");
                }

                foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75 })
                {
                    var suffix = (int)Math.Round(scale * 100);
                    var path = Path.Combine(outputDirectory, $"app-{theme}-{language}-{tabNames[index]}-scale{suffix}.png");
                    SaveSnapshot(window, path, scale, Application.Current.TryFindResource("Layal.Background") as Brush);
                    lines.Add($"{Path.GetFileName(path)} | tab={tabNames[index]} | theme={theme} | language={language} | renderScale={scale:0.00} | windowDips={window.Width:0}x{window.Height:0} | contentDips={windowContent.ActualWidth:0}x{windowContent.ActualHeight:0}");
                }
            }

            // Add a smaller window pass to expose clipped controls and fixed-layout assumptions.
            var fullContentWidth = windowContent.ActualWidth;
            var fullContentHeight = windowContent.ActualHeight;
            window.Width = 900;
            window.Height = 560;
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            windowContent.UpdateLayout();

            Assert.IsTrue(windowContent.ActualWidth < fullContentWidth,
                $"Window width did not shrink for compact capture ({theme}/{language}).");
            Assert.IsTrue(windowContent.ActualHeight < fullContentHeight,
                $"Window height did not shrink for compact capture ({theme}/{language}).");

            for (var index = 0; index < tabNames.Length; index++)
            {
                tabs.SelectedIndex = index;
                tabs.UpdateLayout();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Keyboard.ClearFocus();

                var selectedTab = tabs.SelectedItem as TabItem
                    ?? throw new InvalidOperationException($"Selected tab '{tabNames[index]}' is not a TabItem at compact size.");
                var tabContent = selectedTab.Content as FrameworkElement
                    ?? throw new InvalidOperationException($"Compact content for tab '{tabNames[index]}' was not found.");
                Assert.IsTrue(tabContent.ActualWidth > 0 && tabContent.ActualHeight > 0,
                    $"Compact tab '{tabNames[index]}' has an empty viewport for theme={theme}, language={language}.");
                if (tabNames[index] == "overview")
                {
                    var metricsGrid = window.FindName("HomeMetricsGrid") as UniformGrid
                        ?? throw new InvalidOperationException("HomeMetricsGrid was not found.");
                    Assert.IsTrue(metricsGrid.ActualWidth <= 880,
                        $"The compact overview must stay within the two-column breakpoint. Width={metricsGrid.ActualWidth:0.0} DIP.");
                    Assert.AreEqual(2, metricsGrid.Columns,
                        $"The overview must switch to two metric cards per row on compact windows ({theme}/{language}).");
                }
                AssertNavigationRailDoesNotOverlapContent(window, $"compact {theme}/{language}/{tabNames[index]}");
                if (tabNames[index] == "chat")
                {
                    var windowRoot = window.FindName("WindowRoot") as Grid
                        ?? throw new InvalidOperationException("WindowRoot was not found.");
                    var compactTitleText = window.FindName("TitleText") as TextBlock
                        ?? throw new InvalidOperationException("TitleText was not found.");
                    var subtitle = window.FindName("SubtitleText") as TextBlock
                        ?? throw new InvalidOperationException("SubtitleText was not found.");
                    var connectionStatus = window.FindName("ConnectionStatusShell") as Border
                        ?? throw new InvalidOperationException("ConnectionStatusShell was not found.");
                    Assert.AreEqual(Visibility.Collapsed, subtitle.Visibility,
                        "The compact title bar must hide its secondary subtitle to avoid overlap.");
                    var titleBounds = compactTitleText.TransformToAncestor(windowRoot)
                        .TransformBounds(new Rect(0, 0, compactTitleText.ActualWidth, compactTitleText.ActualHeight));
                    var statusBounds = connectionStatus.TransformToAncestor(windowRoot)
                        .TransformBounds(new Rect(0, 0, connectionStatus.ActualWidth, connectionStatus.ActualHeight));
                    Assert.IsTrue(titleBounds.Right + 4 <= statusBounds.Left,
                        "The compact application title must not overlap the connection-status indicator.");

                    var chatSurface = window.FindName("ChatConversationSurface") as Grid
                        ?? throw new InvalidOperationException("ChatConversationSurface was not found.");
                    var weatherCard = window.FindName("ClockWeatherCard") as Border
                        ?? throw new InvalidOperationException("ClockWeatherCard was not found.");
                    var welcomePanel = window.FindName("ChatWelcomePanel") as Border
                        ?? throw new InvalidOperationException("ChatWelcomePanel was not found.");
                    var welcomeEyebrow = window.FindName("ChatWelcomeEyebrow") as TextBlock
                        ?? throw new InvalidOperationException("ChatWelcomeEyebrow was not found.");
                    var emptyDescription = window.FindName("ChatEmptyStateText") as TextBlock
                        ?? throw new InvalidOperationException("ChatEmptyStateText was not found.");
                    Assert.AreEqual(Visibility.Collapsed, weatherCard.Visibility,
                        "The compact chat layout must free vertical space by hiding its secondary weather card.");
                    Assert.AreEqual(Visibility.Visible, welcomePanel.Visibility,
                        "The welcome canvas must remain visible at compact window size.");
                    Assert.AreEqual(Visibility.Collapsed, welcomeEyebrow.Visibility,
                        "The secondary welcome eyebrow must collapse at compact size to protect the suggestion row.");
                    Assert.IsTrue(emptyDescription.ActualHeight <= 22,
                        $"The compact localized welcome description must stay on one short line ({theme}/{language}); " +
                        $"actual height={emptyDescription.ActualHeight:0.0} DIP.");

                    foreach (var suggestionName in new[]
                    {
                        "ChatSuggestionPlanButton",
                        "ChatSuggestionSummaryButton",
                        "ChatSuggestionIdeasButton"
                    })
                    {
                        var suggestion = window.FindName(suggestionName) as Button
                            ?? throw new InvalidOperationException($"{suggestionName} was not found.");
                        Assert.IsTrue(suggestion.IsVisible && suggestion.ActualHeight >= 40,
                            $"{suggestionName} must remain visible with a 40-DIP touch target in compact chat.");
                        var bounds = suggestion.TransformToAncestor(chatSurface)
                            .TransformBounds(new Rect(0, 0, suggestion.ActualWidth, suggestion.ActualHeight));
                        Assert.IsTrue(bounds.Top >= -0.5 && bounds.Bottom <= chatSurface.ActualHeight + 0.5,
                            $"{suggestionName} is clipped by the compact chat viewport ({theme}/{language}).");
                        var panelBounds = suggestion.TransformToAncestor(welcomePanel)
                            .TransformBounds(new Rect(0, 0, suggestion.ActualWidth, suggestion.ActualHeight));
                        Assert.IsTrue(panelBounds.Top >= -0.5 && panelBounds.Bottom <= welcomePanel.ActualHeight + 0.5,
                            $"{suggestionName} is clipped inside the compact welcome card ({theme}/{language}). " +
                            $"ButtonBottom={panelBounds.Bottom:0.0}, CardHeight={welcomePanel.ActualHeight:0.0}.");
                    }
                }

                var compactPath = Path.Combine(outputDirectory, $"app-compact-{theme}-{language}-{tabNames[index]}-scale100.png");
                SaveSnapshot(window, compactPath, 1.0, Application.Current.TryFindResource("Layal.Background") as Brush);
                lines.Add($"{Path.GetFileName(compactPath)} | tab={tabNames[index]} | theme={theme} | language={language} | windowDips={window.Width:0}x{window.Height:0} | contentDips={windowContent.ActualWidth:0}x{windowContent.ActualHeight:0} | compactResize=true");
            }

            window.Width = 1320;
            window.Height = 860;
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }

        // Render the actual mascot Visual3D tree under representative task and social states.
        var mascot = new FloatingMascotWindow { ShowInTaskbar = false };
        AppRuntime.MascotWindow = mascot;
        mascot.Show();
        mascot.UpdateLayout();
        AssertMascotHighContrastAppearance(mascot);
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
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75 })
            {
                var suffix = (int)Math.Round(scale * 100);
                var path = Path.Combine(outputDirectory, $"mascot-{mood}-scale{suffix}.png");
                SaveSnapshot(mascot, path, scale);
                lines.Add($"{Path.GetFileName(path)} | mood={mood} | renderScale={scale:0.00} | dips={mascot.ActualWidth:0}x{mascot.ActualHeight:0}");
            }
        }

        AssertInteractionAndSettingsPersistence(window, mascot, temp.Path);
        lines.Add("Interactive settings regression: language/theme/navigation, mascot toggles, size/opacity, opt-in window inspection, provider validation/persistence, and show/hide passed.");

        AssertEscapeCancelsActiveChatAndAgentOperations(window);
        AssertPrivateChatDoesNotPersistMessagesAndRestoresSavedConversation(window, temp.Path);
        AssertPrivacyShieldClearsUnsubmittedClipboardAttachments(window);
        AssertPrivacyShieldRejectsLateCameraFrames(window);
        lines.Add("AYO safety regression: Escape cancellation and non-persistent private-chat history passed.");
        AssertAttachmentControlMutualExclusion(window);
        lines.Add("Attachment regression: image and document inputs stay mutually exclusive and respect privacy/preview state.");

        File.WriteAllLines(Path.Combine(outputDirectory, "manifest.txt"), lines);
        AppRuntime.IsExiting = true;
        mascot.Close();
        window.Close();
        app.Shutdown();
        App.SuppressStartupForTests = false;
    }

    private static void AssertKeyboardAccessibility(MainWindow window, Button primaryButton, TextBox chatInput)
    {
        var focusStyle = Application.Current.TryFindResource("Layal.KeyboardFocusVisual") as Style;
        Assert.IsNotNull(focusStyle, "The shared keyboard-focus visual resource must be defined.");
        AssertStyleUsesKeyboardFocusRing(primaryButton.Style, focusStyle!, "Button");

        var checkbox = window.FindName("AnimationsCheckbox") as CheckBox
            ?? throw new InvalidOperationException("AnimationsCheckbox was not found.");
        AssertStyleUsesKeyboardFocusRing(checkbox.Style, focusStyle!, "CheckBox");
        var slider = window.FindName("MascotSizeSlider") as Slider
            ?? throw new InvalidOperationException("MascotSizeSlider was not found.");
        AssertStyleUsesKeyboardFocusRing(slider.Style, focusStyle!, "Slider");
        var homeTab = window.FindName("HomeTab") as TabItem
            ?? throw new InvalidOperationException("HomeTab was not found.");
        var navigationStyle = Application.Current.TryFindResource("Layal.NavTabItemStyle") as Style;
        Assert.IsNotNull(navigationStyle, "The navigation tab style must be available.");
        AssertStyleUsesKeyboardFocusRing(navigationStyle!, focusStyle!, "TabItem");

        foreach (var controlType in new[] { typeof(ComboBoxItem), typeof(ListBoxItem) })
        {
            var itemStyle = Application.Current.TryFindResource(controlType) as Style;
            Assert.IsNotNull(itemStyle, $"Implicit {controlType.Name} style must be available.");
            AssertStyleUsesKeyboardFocusRing(itemStyle!, focusStyle!, controlType.Name);
        }

        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(chatInput)),
            "Chat input must expose an accessible name.");
        Assert.AreEqual("ChatInput",
            AutomationProperties.GetAutomationId(chatInput),
            "The chat editor must expose a stable UI Automation id for keyboard focus verification.");

        var mainTabs = window.FindName("MainTabs") as TabControl
            ?? throw new InvalidOperationException("MainTabs was not found.");
        mainTabs.ApplyTemplate();
        mainTabs.UpdateLayout();
        var tabPeer = UIElementAutomationPeer.CreatePeerForElement(mainTabs)
            ?? throw new InvalidOperationException("The main navigation UI Automation peer was not created.");
        var pendingPeers = new Stack<AutomationPeer>(tabPeer.GetChildren() ?? new List<AutomationPeer>());
        var visitedPeers = new HashSet<AutomationPeer>();
        var chatEditorExposed = false;
        while (pendingPeers.Count > 0)
        {
            var peer = pendingPeers.Pop();
            if (!visitedPeers.Add(peer))
                continue;
            if (string.Equals(peer.GetAutomationId(), "ChatInput", StringComparison.Ordinal))
            {
                chatEditorExposed = true;
                break;
            }
            foreach (var child in peer.GetChildren() ?? new List<AutomationPeer>())
                pendingPeers.Push(child);
        }
        Assert.IsTrue(chatEditorExposed,
            "The content-only navigation peer must expose the selected chat editor to UI Automation.");

        var clipboardButton = window.FindName("ReadClipboardButton") as Button
            ?? throw new InvalidOperationException("ReadClipboardButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(clipboardButton)),
            "Clipboard text review must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(clipboardButton)),
            "Clipboard text review must disclose its privacy behaviour.");
        var clipboardImageButton = window.FindName("PasteClipboardImageButton") as Button
            ?? throw new InvalidOperationException("PasteClipboardImageButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(clipboardImageButton)),
            "Clipboard image attachment must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(clipboardImageButton)),
            "Clipboard image attachment must disclose consent, size limits, and no-auto-send behaviour.");
        var remindersButton = window.FindName("ManageRemindersButton") as Button
            ?? throw new InvalidOperationException("ManageRemindersButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(remindersButton)),
            "The reminder manager must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(remindersButton)),
            "The reminder manager must disclose where reminders are stored and how notifications work.");
        var routinesButton = window.FindName("ManageRoutinesButton") as Button
            ?? throw new InvalidOperationException("ManageRoutinesButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(routinesButton)),
            "The routine manager must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(routinesButton)),
            "The routine manager must disclose that choosing a routine does not run it.");
        var performanceModeCheckbox = window.FindName("PerformanceModeCheckbox") as CheckBox
            ?? throw new InvalidOperationException("PerformanceModeCheckbox was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(performanceModeCheckbox)),
            "Performance mode must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(performanceModeCheckbox)),
            "Performance mode must describe its effect on animations and 3D rendering.");
        var themePresetSelector = window.FindName("ThemePresetSelector") as ComboBox
            ?? throw new InvalidOperationException("ThemePresetSelector was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(themePresetSelector)),
            "Theme preset selector must expose an accessible name.");
        Assert.AreEqual(4, themePresetSelector.Items.Count,
            "The app should offer four named presets (Classic, Ocean, Forest, Sunset).");
        var hotkeySelector = window.FindName("QuickAskHotkeySelector") as ComboBox
            ?? throw new InvalidOperationException("QuickAskHotkeySelector was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(hotkeySelector)),
            "Quick Ask shortcut selector must expose an accessible name.");
        Assert.AreEqual(3, hotkeySelector.Items.Count, "Three safe shortcut presets should be available.");
        var hotkeyStatus = window.FindName("QuickAskHotkeyStatus") as TextBlock
            ?? throw new InvalidOperationException("QuickAskHotkeyStatus was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(hotkeyStatus)),
            "Quick Ask shortcut registration status must be accessible.");
        var gameModeButton = window.FindName("GameModeButton") as Button
            ?? throw new InvalidOperationException("GameModeButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(gameModeButton)),
            "Manual game mode must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(gameModeButton)),
            "Manual game mode must disclose that it does not inspect or control games.");
        var workspaceHubButton = window.FindName("WorkspaceHubButton") as Button
            ?? throw new InvalidOperationException("WorkspaceHubButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(workspaceHubButton)),
            "Workspace hub must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(workspaceHubButton)),
            "Workspace hub must disclose local indexing and export privacy.");
        var gmailButton = window.FindName("GmailInboxButton") as Button
            ?? throw new InvalidOperationException("GmailInboxButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(gmailButton)),
            "The Gmail inbox button must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(gmailButton)),
            "The Gmail inbox button must disclose read-only access and manual sharing.");
        var personaSelector = window.FindName("AssistantPersonaSelector") as ComboBox
            ?? throw new InvalidOperationException("AssistantPersonaSelector was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(personaSelector)),
            "The assistant persona selector must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(personaSelector)),
            "The assistant persona selector must disclose that style does not change permissions.");
        Assert.AreEqual(3, personaSelector.Items.Count,
            "The app should offer three named response styles.");

        var privacyShieldButton = window.FindName("PrivacyShieldButton") as Button
            ?? throw new InvalidOperationException("PrivacyShieldButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(privacyShieldButton)),
            "The master privacy shield must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(privacyShieldButton)),
            "The master privacy shield must disclose the capture operations it blocks.");
        var checkInToggle = window.FindName("ProactiveCheckInEnabledCheckBox") as CheckBox
            ?? throw new InvalidOperationException("ProactiveCheckInEnabledCheckBox was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(checkInToggle)),
            "Scheduled check-ins must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(checkInToggle)),
            "Scheduled check-ins must disclose that they do not inspect the screen or call AI.");
        var inspectElementButton = window.FindName("InspectHoveredElementButton") as Button
            ?? throw new InvalidOperationException("InspectHoveredElementButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(inspectElementButton)),
            "UI element inspection must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(inspectElementButton)),
            "UI element inspection must disclose that no field values or screenshots are captured.");
        var loreWebButton = window.FindName("LoreWebButton") as Button
            ?? throw new InvalidOperationException("LoreWebButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(loreWebButton)),
            "The local 3D memory web must expose an accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(loreWebButton)),
            "The local 3D memory web must disclose that links are approximate and private.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(homeTab)),
            "Navigation tabs must expose language-appropriate accessible names.");
        foreach (var dockButtonName in new[]
        {
            "DockChatButton", "DockOverviewButton", "DockTasksButton", "DockSkillsButton",
            "DockCamerasButton", "DockMemoryButton", "DockApiButton", "DockSettingsButton"
        })
        {
            var dockButton = window.FindName(dockButtonName) as Button
                ?? throw new InvalidOperationException($"{dockButtonName} was not found.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(
                System.Windows.Automation.AutomationProperties.GetName(dockButton)),
                $"Navigation destination '{dockButtonName}' must expose a localized accessible name.");
        }
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetHelpText(window.FindName("ChatHistoryList") as DependencyObject)),
            "The saved-history panel must explain how to resume a conversation.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(window.FindName("BaseUrlInput") as DependencyObject)),
            "Provider fields must expose accessible names.");
    }

    private static void AssertAccessibleStatusRegions(MainWindow window)
    {
        var chatActivity = window.FindName("ChatActivityStatus") as TextBlock
            ?? throw new InvalidOperationException("ChatActivityStatus was not found.");
        var providerStatus = window.FindName("ChatProviderStatus") as TextBlock
            ?? throw new InvalidOperationException("ChatProviderStatus was not found.");
        var connectionStatus = window.FindName("ConnectionStatusText") as TextBlock
            ?? throw new InvalidOperationException("ConnectionStatusText was not found.");
        var taskResult = window.FindName("TaskResult") as TextBox
            ?? throw new InvalidOperationException("TaskResult was not found.");

        var liveStatusNames = new[]
        {
            "ConnectionStatusText", "HomeResult", "ChatActivityStatus", "ChatCopyStatus",
            "TaskResult", "TaskCopyStatus", "TaskEventsList", "SkillsResult",
            "MemoryResult", "ProviderActionStatus"
        };
        foreach (var name in liveStatusNames)
        {
            var status = window.FindName(name) as FrameworkElement
                ?? throw new InvalidOperationException($"Live status element '{name}' was not found.");
            Assert.AreEqual(System.Windows.Automation.AutomationLiveSetting.Polite,
                System.Windows.Automation.AutomationProperties.GetLiveSetting(status),
                $"Live status '{name}' must use non-interruptive announcements.");
        }
        Assert.AreEqual(System.Windows.Automation.AutomationLiveSetting.Off,
            System.Windows.Automation.AutomationProperties.GetLiveSetting(providerStatus),
            "Provider setup summary must not repeat every transient chat activity announcement.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(chatActivity)),
            "Chat activity must have a localized accessible name.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(
            System.Windows.Automation.AutomationProperties.GetName(providerStatus)),
            "Provider status must have a separate accessible name.");

        var setActivityStatus = typeof(MainWindow).GetMethod("SetChatActivityStatus", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SetChatActivityStatus was not found.");
        var refreshProviderStatus = typeof(MainWindow).GetMethod("RefreshProviderStatus", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RefreshProviderStatus was not found.");
        const string marker = "activity status remains separate";
        setActivityStatus.Invoke(window, new object[] { marker });
        refreshProviderStatus.Invoke(window, null);
        Assert.AreEqual(marker, BidiTextNormalizer.StripDirectionalControls(chatActivity.Text),
            "Refreshing provider connectivity must not overwrite the chat's current activity state.");
        Assert.AreNotEqual(marker, BidiTextNormalizer.StripDirectionalControls(providerStatus.Text),
            "Provider connectivity must remain a separate status from chat activity.");
        setActivityStatus.Invoke(window, new object[] { string.Empty });
    }

    private static void AssertMascotHighContrastAppearance(FloatingMascotWindow mascot)
    {
        var applyAppearance = typeof(FloatingMascotWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "ApplySystemAccessibilityAppearance" && method.GetParameters().Length == 1);
        var shell = mascot.FindName("Shell") as Border
            ?? throw new InvalidOperationException("Mascot shell border was not found.");
        var halo = mascot.FindName("GlowHalo") as FrameworkElement
            ?? throw new InvalidOperationException("Mascot glow halo was not found.");
        var effects = mascot.FindName("CinematicEffectsLayer") as FrameworkElement
            ?? throw new InvalidOperationException("Mascot cinematic effects layer was not found.");

        var originalBrush = shell.BorderBrush;
        var originalThickness = shell.BorderThickness;
        var originalEffect = shell.Effect;
        var originalHaloVisibility = halo.Visibility;
        var originalEffectsVisibility = effects.Visibility;
        var systemHighContrast = SystemParameters.HighContrast;

        applyAppearance.Invoke(mascot, new object[] { true });

        Assert.AreEqual(new Thickness(3), shell.BorderThickness,
            "High Contrast must give the floating mascot a visible solid outline.");
        Assert.AreEqual(SystemColors.WindowTextColor, ((SolidColorBrush)shell.BorderBrush).Color,
            "The mascot outline must use the Windows system text color.");
        Assert.IsNull(shell.Effect, "High Contrast must remove the mascot's decorative shell shadow.");
        Assert.AreEqual(Visibility.Collapsed, halo.Visibility,
            "High Contrast must suppress the low-contrast decorative glow.");
        Assert.AreEqual(Visibility.Collapsed, effects.Visibility,
            "High Contrast must suppress decorative particles.");
        
        applyAppearance.Invoke(mascot, new object[] { systemHighContrast });
        // The real system-preference handler also refreshes the performance budget;
        // this rebuilds decorative particles after High Contrast cleared them.
        mascot.ApplyPerformanceMode();
        Assert.AreEqual(originalBrush, shell.BorderBrush, "The mascot border must restore to the system-selected appearance.");
        Assert.AreEqual(originalThickness, shell.BorderThickness, "The mascot border thickness must restore after the forced test state.");
        Assert.AreEqual(originalEffect, shell.Effect, "The mascot shell effect must restore after the forced test state.");
        Assert.AreEqual(originalHaloVisibility, halo.Visibility, "The mascot glow visibility must restore after the forced test state.");
        Assert.AreEqual(originalEffectsVisibility, effects.Visibility, "The cinematic layer visibility must restore after the forced test state.");
    }


    private static void AssertInitialWindowFitsWorkArea(MainWindow window)
    {
        var workArea = SystemParameters.WorkArea;
        Assert.IsTrue(workArea.Width > 0 && workArea.Height > 0,
            "Windows must report a usable work area for responsive sizing.");
        Assert.IsTrue(window.Width <= workArea.Width,
            $"Initial window width {window.Width:0.##} DIPs exceeds work area width {workArea.Width:0.##} DIPs.");
        Assert.IsTrue(window.Height <= workArea.Height,
            $"Initial window height {window.Height:0.##} DIPs exceeds work area height {workArea.Height:0.##} DIPs.");
        Assert.IsTrue(window.Left >= workArea.Left && window.Top >= workArea.Top &&
                      window.Left + window.Width <= workArea.Right &&
                      window.Top + window.Height <= workArea.Bottom,
            "Initial main window must be positioned fully inside the Windows work area.");
        Assert.IsTrue(window.MinWidth <= window.Width && window.MinHeight <= window.Height,
            "Responsive initial sizing must keep minimum dimensions at or below the applied window dimensions.");
    }


    private static void AssertLanguageLayoutContracts(MainWindow window, string language)
    {
        var ar = language == "ar";
        var expectedWritingDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var proseFields = new[]
        {
            "ChatInput", "TaskInput", "MemoryTitleInput", "MemoryContentInput", "MemorySearchInput"
        };

        foreach (var fieldName in proseFields)
        {
            var field = window.FindName(fieldName) as Control
                ?? throw new InvalidOperationException($"Expected prose input '{fieldName}' was not found.");
            Assert.AreEqual(expectedWritingDirection, field.FlowDirection,
                $"Prose field '{fieldName}' has the wrong flow direction for {language}.");
        }

        foreach (var fieldName in new[] { "BaseUrlInput", "ModelInput", "ApiKeyInput", "GitHubSkillUrlInput" })
        {
            var field = window.FindName(fieldName) as Control
                ?? throw new InvalidOperationException($"Expected technical input '{fieldName}' was not found.");
            Assert.AreEqual(FlowDirection.LeftToRight, field.FlowDirection,
                $"Technical field '{fieldName}' must remain LTR for {language}.");
        }

        Assert.AreEqual(FlowDirection.LeftToRight, window.FlowDirection,
            "The whole window must not be mirrored to achieve Arabic RTL text.");

        var mainTabs = window.FindName("MainTabs") as TabControl
            ?? throw new InvalidOperationException("MainTabs was not found.");
        Assert.AreEqual(expectedWritingDirection, mainTabs.FlowDirection,
            $"The page host must retain the selected language flow direction for {language}.");
        var dockItems = window.FindName("DockNavigationItems") as StackPanel
            ?? throw new InvalidOperationException("DockNavigationItems was not found.");
        Assert.AreEqual(expectedWritingDirection, dockItems.FlowDirection,
            $"The bottom navigation dock must follow the selected writing direction for {language}.");
        var historyPanel = window.FindName("ChatHistoryPanel") as Border
            ?? throw new InvalidOperationException("ChatHistoryPanel was not found.");
        Assert.AreEqual(expectedWritingDirection, historyPanel.FlowDirection,
            $"The chat history panel must follow the selected writing direction for {language}.");
        Assert.AreEqual(ar ? 1 : 0, Grid.GetColumn(historyPanel),
            $"The saved-history panel must appear on the correct language side for {language}.");
        var chatSurface = window.FindName("ChatConversationSurface") as Grid
            ?? throw new InvalidOperationException("ChatConversationSurface was not found.");
        Assert.AreEqual(ar ? 0 : 1, Grid.GetColumn(chatSurface),
            $"The main conversation surface must retain the opposite column for {language}.");
        var chatLayout = window.FindName("ChatTabLayout") as Grid
            ?? throw new InvalidOperationException("ChatTabLayout was not found.");
        var historyColumnWidth = chatLayout.ColumnDefinitions[Grid.GetColumn(historyPanel)].Width;
        var conversationColumnWidth = chatLayout.ColumnDefinitions[Grid.GetColumn(chatSurface)].Width;
        Assert.AreEqual(GridUnitType.Auto, historyColumnWidth.GridUnitType,
            $"The saved-history panel must use an auto-sized column in {language}.");
        Assert.AreEqual(GridUnitType.Star, conversationColumnWidth.GridUnitType,
            $"The conversation surface must use the flexible star-sized column in {language}.");
        var dockHost = window.FindName("BottomDockHost") as Grid
            ?? throw new InvalidOperationException("BottomDockHost was not found.");
        Assert.AreEqual(VerticalAlignment.Stretch, dockHost.VerticalAlignment,
            "The persistent navigation rail must span the available application height.");
        Assert.AreEqual(HorizontalAlignment.Left, dockHost.HorizontalAlignment,
            "The persistent navigation rail must remain anchored to the left edge of the application.");

        var taskResult = window.FindName("TaskResult") as TextBox
            ?? throw new InvalidOperationException("TaskResult was not found.");
        Assert.AreEqual(ar ? "نتيجة المهمة" : "Task result",
            System.Windows.Automation.AutomationProperties.GetName(taskResult),
            "Task result must have a localized accessible name.");
        Assert.AreEqual(ar
                ? "يعرض النتيجة النهائية وحالة المهمة، بما في ذلك التحذيرات أو الإخفاق."
                : "Shows the final task result and its status, including warnings or failures.",
            System.Windows.Automation.AutomationProperties.GetHelpText(taskResult),
            "Task result must expose localized context to assistive technology.");
        Assert.AreEqual(System.Windows.Automation.AutomationLiveSetting.Polite,
            System.Windows.Automation.AutomationProperties.GetLiveSetting(taskResult),
            "Task result must announce polite live updates to assistive technology.");
    }


    private static void AssertChatMessageDirectionContracts(MainWindow window, MethodInfo applyLanguage)
    {
        var arabicMixed = new ChatMessage
        {
            Role = "user",
            Content = "راجع الرابط https://example.com/v1/models/gpt-4o مع المعرّف model-2026"
        };
        var englishOnly = new ChatMessage
        {
            Role = "assistant",
            Content = "Check https://example.com/v1/models/gpt-4o and model-2026"
        };

        window.ChatMessages.Add(arabicMixed);
        window.ChatMessages.Add(englishOnly);
        applyLanguage.Invoke(window, null);

        Assert.AreEqual(FlowDirection.RightToLeft, arabicMixed.Direction,
            "A message containing Arabic with Latin technical tokens must use RTL paragraph flow.");
        Assert.AreEqual(FlowDirection.LeftToRight, englishOnly.Direction,
            "An English-only message must retain LTR paragraph flow.");

        window.ChatMessages.Remove(arabicMixed);
        window.ChatMessages.Remove(englishOnly);
        applyLanguage.Invoke(window, null);
        AssertEmptyStateLocalizationAndVisibility(window, AppRuntime.Settings.Language);
    }


    private static void AssertClockWeatherDashboard(MainWindow window)
    {
        var clock = window.FindName("ClockTimeText") as TextBlock
            ?? throw new InvalidOperationException("ClockTimeText was not found.");
        var date = window.FindName("CurrentDateText") as TextBlock
            ?? throw new InvalidOperationException("CurrentDateText was not found.");
        var weather = window.FindName("WeatherSummaryText") as TextBlock
            ?? throw new InvalidOperationException("WeatherSummaryText was not found.");
        var weatherLocation = window.FindName("WeatherLocationInput") as TextBox
            ?? throw new InvalidOperationException("WeatherLocationInput was not found.");
        var refreshButton = window.FindName("RefreshWeatherButton") as Button
            ?? throw new InvalidOperationException("RefreshWeatherButton was not found.");
        var useMyLocationButton = window.FindName("UseMyLocationButton") as Button
            ?? throw new InvalidOperationException("UseMyLocationButton was not found.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(clock.Text), "The dashboard clock must display the system-local time.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(date.Text), "The dashboard must display the system-local date.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(weather.Text), "The dashboard must explain the weather state, even before a city is configured.");
        Assert.IsTrue(refreshButton.IsEnabled, "Weather refresh must be available after the initial display.");
        Assert.IsTrue(useMyLocationButton.IsEnabled, "Automatic device-location detection must be available.");
        Assert.IsNotNull(weatherLocation, "The city setting must be available without requiring precise device location.");
    }

    private static void AssertEmptyStateLocalizationAndVisibility(MainWindow window, string language)
    {
        var isArabic = language == "ar";
        var chatEmptyText = window.FindName("ChatEmptyStateText") as TextBlock
            ?? throw new InvalidOperationException("ChatEmptyStateText was not found.");
        var chatWelcomePanel = window.FindName("ChatWelcomePanel") as Border
            ?? throw new InvalidOperationException("ChatWelcomePanel was not found.");
        var welcomeEyebrow = window.FindName("ChatWelcomeEyebrow") as TextBlock
            ?? throw new InvalidOperationException("ChatWelcomeEyebrow was not found.");
        var isCompact = welcomeEyebrow.Visibility == Visibility.Collapsed;
        var expectedWelcomeCopy = isCompact
            ? (isArabic ? "اكتب سؤالك أو اختر اقتراحًا." : "Choose a prompt or type your question.")
            : (isArabic
                ? "اكتب سؤالك مباشرة، أو اختر نقطة بداية وسأساعدك في تطويرها."
                : "Type a question or choose a prompt starter to fill the message field. Messages appear here after sending.");
        Assert.AreEqual(expectedWelcomeCopy,
            BidiTextNormalizer.StripDirectionalControls(chatEmptyText.Text),
            "Chat welcome copy must match the selected language and available viewport height.");
        Assert.AreEqual(isArabic ? "حالة المحادثة الفارغة" : "Chat empty state",
            System.Windows.Automation.AutomationProperties.GetName(chatEmptyText));
        Assert.AreEqual(window.ChatMessages.Count == 0 ? Visibility.Visible : Visibility.Collapsed,
            chatWelcomePanel.Visibility,
            "The chat welcome canvas must only appear while the conversation is empty.");
        Assert.AreEqual(TextWrapping.Wrap, chatEmptyText.TextWrapping);
        AssertEmptyState(window, "TaskEventsEmptyStateText", window.TaskEvents.Count,
            isArabic ? "لا يوجد سجل نشاط بعد. ابدأ مهمة لعرض أحداث تنفيذها هنا." : "No task activity yet. Start a task to see its events here.",
            isArabic ? "حالة سجل المهام الفارغ" : "Task activity empty state");
        AssertEmptyState(window, "RecentTasksEmptyStateText", window.RecentTasks.Count,
            isArabic ? "لا توجد مهام سابقة بعد." : "No previous tasks yet.",
            isArabic ? "حالة المهام السابقة الفارغة" : "Recent tasks empty state");
        AssertEmptyState(window, "SkillsEmptyStateText", window.InstalledSkills.Count,
            isArabic ? "لا توجد مهارات مثبتة. استورد ملف ZIP أو مجلدًا من GitHub للبدء." : "No skills installed. Import a ZIP or GitHub folder to get started.",
            isArabic ? "حالة قائمة المهارات الفارغة" : "Skills empty state");
        AssertEmptyState(window, "MemoryEmptyStateText", window.MemoryNotes.Count,
            isArabic ? "لا توجد ملاحظات محفوظة. أنشئ ملاحظة جديدة للبدء." : "No saved notes yet. Create a note to get started.",
            isArabic ? "حالة الذاكرة الفارغة" : "Memory empty state");
        AssertMemoryManagementControls(window, isArabic);
    }


    private static void AssertMemoryManagementControls(MainWindow window, bool isArabic)
    {
        var archived = window.FindName("MemoryShowArchivedCheckBox") as CheckBox
            ?? throw new InvalidOperationException("MemoryShowArchivedCheckBox was not found.");
        var categoryFilter = window.FindName("MemoryCategoryFilter") as ComboBox
            ?? throw new InvalidOperationException("MemoryCategoryFilter was not found.");
        var import = window.FindName("ImportMemoryButton") as Button
            ?? throw new InvalidOperationException("ImportMemoryButton was not found.");
        var export = window.FindName("ExportMemoryButton") as Button
            ?? throw new InvalidOperationException("ExportMemoryButton was not found.");
        var clear = window.FindName("ClearMemoryFiltersButton") as Button
            ?? throw new InvalidOperationException("ClearMemoryFiltersButton was not found.");
        var count = window.FindName("MemoryCountText") as TextBlock
            ?? throw new InvalidOperationException("MemoryCountText was not found.");
        var archiveAction = window.FindName("ArchiveMemoryButton") as Button
            ?? throw new InvalidOperationException("ArchiveMemoryButton was not found.");
        var deleteAction = window.FindName("DeleteMemoryButton") as Button
            ?? throw new InvalidOperationException("DeleteMemoryButton was not found.");

        Assert.AreEqual(isArabic ? "عرض المؤرشفة" : "Include archived", BidiTextNormalizer.StripDirectionalControls(archived.Content as string));
        Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(archived)));
        Assert.IsTrue(categoryFilter.Items.Count >= 1, "The category filter must always include All categories.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(categoryFilter)));
        Assert.AreEqual(isArabic ? "استيراد JSON" : "Import JSON", BidiTextNormalizer.StripDirectionalControls(import.Content as string));
        Assert.AreEqual(isArabic ? "تصدير JSON" : "Export JSON", BidiTextNormalizer.StripDirectionalControls(export.Content as string));
        Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(import)));
        Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(export)));
        Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(clear)));
        Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(count)));
        Assert.IsFalse(archiveAction.IsEnabled, "Archive must be disabled until a note is selected.");
        Assert.IsFalse(deleteAction.IsEnabled, "Delete must be disabled until a note is selected.");
        Assert.AreEqual(120, ((TextBox)window.FindName("MemoryTitleInput")!).MaxLength);
        Assert.AreEqual(12000, ((TextBox)window.FindName("MemoryContentInput")!).MaxLength);
    }

    private static void AssertEmptyState(
        MainWindow window,
        string elementName,
        int itemCount,
        string expectedText,
        string expectedAutomationName)
    {
        var emptyState = window.FindName(elementName) as TextBlock
            ?? throw new InvalidOperationException($"Empty-state element '{elementName}' was not found.");
        Assert.AreEqual(expectedText, BidiTextNormalizer.StripDirectionalControls(emptyState.Text),
            $"Empty state '{elementName}' must be localized in the selected language.");
        Assert.AreEqual(expectedAutomationName,
            System.Windows.Automation.AutomationProperties.GetName(emptyState),
            $"Empty state '{elementName}' must expose a localized UI Automation name.");
        Assert.AreEqual(itemCount == 0 ? Visibility.Visible : Visibility.Collapsed, emptyState.Visibility,
            $"Empty state '{elementName}' must only be visible when its source collection is empty.");
        Assert.AreEqual(TextWrapping.Wrap, emptyState.TextWrapping,
            $"Empty state '{elementName}' must support multiline copy at high scaling.");
    }

    private static void AssertStyleUsesKeyboardFocusRing(Style style, Style focusStyle, string controlName)
    {
        var focusSetter = style.Setters.OfType<Setter>()
            .FirstOrDefault(setter => setter.Property == Control.FocusVisualStyleProperty);
        Assert.IsNotNull(focusSetter, $"{controlName} style must declare its keyboard focus visual.");
        Assert.AreSame(focusStyle, focusSetter!.Value,
            $"{controlName} style must use the shared keyboard-focus ring.");
    }

    private static string? FindVisualText(DependencyObject root)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock textBlock && !string.IsNullOrEmpty(textBlock.Text))
                return textBlock.Text;

            var nested = FindVisualText(child);
            if (!string.IsNullOrEmpty(nested))
                return nested;
        }

        return null;
    }

    private static void AssertOpaqueWindowShell(MainWindow window, Grid rootGrid, string state)
    {
        var expectedBackground = (Color)Application.Current.Resources["Layal.Color.Background"];
        var windowBrush = window.Background as SolidColorBrush
            ?? throw new InvalidOperationException($"The native window background must resolve to a solid brush ({state}).");
        var rootBrush = rootGrid.Background as SolidColorBrush
            ?? throw new InvalidOperationException($"The application shell background must resolve to a solid brush ({state}).");

        Assert.AreEqual((byte)255, windowBrush.Color.A,
            $"The native window surface must remain fully opaque ({state}).");
        Assert.AreEqual(expectedBackground, windowBrush.Color,
            $"The native window surface must follow the active theme ({state}).");
        Assert.AreEqual((byte)255, rootBrush.Color.A,
            $"The WPF client shell must remain fully opaque ({state}).");
        Assert.AreEqual(expectedBackground, rootBrush.Color,
            $"The WPF client shell must match the active theme surface ({state}).");
    }

    private static void AssertNavigationRailDoesNotOverlapContent(MainWindow window, string layoutName)
    {
        var root = window.FindName("WindowRoot") as Grid
            ?? throw new InvalidOperationException("WindowRoot was not found.");
        var rail = window.FindName("BottomDockShell") as Border
            ?? throw new InvalidOperationException("BottomDockShell was not found.");
        var tabs = window.FindName("MainTabs") as FrameworkElement
            ?? throw new InvalidOperationException("MainTabs was not found.");

        var railBounds = rail.TransformToAncestor(root)
            .TransformBounds(new Rect(0, 0, rail.ActualWidth, rail.ActualHeight));
        var contentBounds = tabs.TransformToAncestor(root)
            .TransformBounds(new Rect(0, 0, tabs.ActualWidth, tabs.ActualHeight));

        Assert.IsTrue(railBounds.Right + 8 <= contentBounds.Left + 0.5,
            $"The navigation rail must leave an 8-DIP content gutter and never cover workspace pages ({layoutName}). " +
            $"RailRight={railBounds.Right:0.0}, ContentLeft={contentBounds.Left:0.0}.");
    }

    private static void AssertPageHeroGradientTracksPalette(string mode)
    {
        var gradient = Application.Current.Resources["Layal.PageHeroGradient"] as LinearGradientBrush
            ?? throw new InvalidOperationException("Layal.PageHeroGradient was not refreshed for the active theme.");
        Assert.AreEqual(3, gradient.GradientStops.Count,
            $"The page hero gradient must retain its three-stop theme structure ({mode}).");
        Assert.AreEqual((Color)Application.Current.Resources["Layal.Color.CardStart"], gradient.GradientStops[0].Color,
            $"The page hero gradient must use the active card start color ({mode}).");
        Assert.AreEqual((Color)Application.Current.Resources["Layal.Color.HeaderMiddle"], gradient.GradientStops[1].Color,
            $"The page hero gradient must use the active header middle color ({mode}).");
        Assert.AreEqual((Color)Application.Current.Resources["Layal.Color.CardEnd"], gradient.GradientStops[2].Color,
            $"The page hero gradient must use the active card end color ({mode}).");
    }

    private static void AssertThemeTogglePersistsLivePalette(MainWindow window, ComboBox historySelector)
    {
        var themeButton = window.FindName("ThemeButton") as Button
            ?? throw new InvalidOperationException("ThemeButton was not found.");
        var applyTheme = typeof(MainWindow).GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyTheme was not found.");
        var applyLanguage = typeof(MainWindow).GetMethod("ApplyLanguage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyLanguage was not found.");
        var toggleTheme = typeof(MainWindow).GetMethod("ThemeButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ThemeButton_Click was not found.");

        // Exercise the actual UI event handler, not just ApplyTheme(), so a broken click
        // binding or a missing settings save cannot slip past visual snapshot generation.
        AppRuntime.Settings.ThemeMode = "light";
        AppRuntime.Settings.Language = "ar";
        applyTheme.Invoke(window, null);
        applyLanguage.Invoke(window, null);

        Assert.AreEqual(Color.FromRgb(243, 246, 255), (Color)Application.Current.Resources["Layal.Color.Background"],
            "The light background palette must be active before the toggle.");
        AssertPageHeroGradientTracksPalette("light before toggle");
        Assert.AreEqual("☾ المظهر الداكن", BidiTextNormalizer.StripDirectionalControls(themeButton.Content as string),
            "In light mode, the button must offer switching to dark mode.");
        AssertThemeContrastAndControlSurfaces("light", "ar", historySelector);

        toggleTheme.Invoke(window, new object?[] { themeButton, new RoutedEventArgs(Button.ClickEvent, themeButton) });

        Assert.AreEqual("dark", AppRuntime.Settings.ThemeMode,
            "Clicking the theme button must switch the selected theme to dark.");
        Assert.AreEqual(Color.FromRgb(38, 52, 81), (Color)Application.Current.Resources["Layal.Color.Background"],
            "Clicking the theme button must apply the dark resource palette.");
        AssertPageHeroGradientTracksPalette("dark after toggle");
        Assert.AreEqual("dark", AppRuntime.SettingsStore.Load().ThemeMode,
            "Clicking the theme button must persist dark mode in settings.json.");
        Assert.AreEqual("☀ المظهر الفاتح", BidiTextNormalizer.StripDirectionalControls(themeButton.Content as string),
            "In dark mode, the button must offer switching to light mode.");
        AssertThemeContrastAndControlSurfaces("dark", "ar", historySelector);

        toggleTheme.Invoke(window, new object?[] { themeButton, new RoutedEventArgs(Button.ClickEvent, themeButton) });

        Assert.AreEqual("light", AppRuntime.Settings.ThemeMode,
            "Clicking the theme button again must restore the light theme.");
        Assert.AreEqual(Color.FromRgb(243, 246, 255), (Color)Application.Current.Resources["Layal.Color.Background"],
            "Switching back must restore the light resource palette.");
        AssertPageHeroGradientTracksPalette("light after toggle");
        Assert.AreEqual("light", AppRuntime.SettingsStore.Load().ThemeMode,
            "Switching back must persist light mode in settings.json.");
        AssertThemeContrastAndControlSurfaces("light", "ar", historySelector);
    }

    private static void AssertInteractionAndSettingsPersistence(MainWindow window, FloatingMascotWindow mascot, string dataRoot)
    {
        var store = new SettingsStore(dataRoot);
        var languageButton = window.FindName("LanguageButton") as Button
            ?? throw new InvalidOperationException("LanguageButton was not found.");
        var tabs = window.FindName("MainTabs") as TabControl
            ?? throw new InvalidOperationException("MainTabs was not found.");
        var homeTab = window.FindName("HomeTab") as TabItem
            ?? throw new InvalidOperationException("HomeTab was not found.");
        var chatTab = window.FindName("ChatTab") as TabItem
            ?? throw new InvalidOperationException("ChatTab was not found.");
        var settingsTab = window.FindName("SettingsTab") as TabItem
            ?? throw new InvalidOperationException("SettingsTab was not found.");
        var goChat = window.FindName("GoChatButton") as Button
            ?? throw new InvalidOperationException("GoChatButton was not found.");
        var goSettings = window.FindName("GoSettingsButton") as Button
            ?? throw new InvalidOperationException("GoSettingsButton was not found.");
        var showMascot = window.FindName("ShowMascotButton") as Button
            ?? throw new InvalidOperationException("ShowMascotButton was not found.");
        var dockChat = window.FindName("DockChatButton") as Button
            ?? throw new InvalidOperationException("DockChatButton was not found.");
        var dockOverview = window.FindName("DockOverviewButton") as Button
            ?? throw new InvalidOperationException("DockOverviewButton was not found.");
        var dockBrandTitle = window.FindName("DockBrandTitle") as TextBlock
            ?? throw new InvalidOperationException("DockBrandTitle was not found.");
        var dockBrandSubtitle = window.FindName("DockBrandSubtitle") as TextBlock
            ?? throw new InvalidOperationException("DockBrandSubtitle was not found.");
        var dockFooterTitle = window.FindName("DockFooterTitle") as TextBlock
            ?? throw new InvalidOperationException("DockFooterTitle was not found.");
        var dockFooterDescription = window.FindName("DockFooterDescription") as TextBlock
            ?? throw new InvalidOperationException("DockFooterDescription was not found.");
        var dockToggle = window.FindName("ToggleBottomDockButton") as Button
            ?? throw new InvalidOperationException("ToggleBottomDockButton was not found.");
        var expandedDock = window.FindName("DockExpandedSurface") as Border
            ?? throw new InvalidOperationException("DockExpandedSurface was not found.");
        var historyToggle = window.FindName("ToggleChatHistoryButton") as Button
            ?? throw new InvalidOperationException("ToggleChatHistoryButton was not found.");
        var historyPanel = window.FindName("ChatHistoryPanel") as Border
            ?? throw new InvalidOperationException("ChatHistoryPanel was not found.");
        var alwaysOnTop = window.FindName("AlwaysOnTopCheckbox") as CheckBox
            ?? throw new InvalidOperationException("AlwaysOnTopCheckbox was not found.");
        var animations = window.FindName("AnimationsCheckbox") as CheckBox
            ?? throw new InvalidOperationException("AnimationsCheckbox was not found.");
        var performanceMode = window.FindName("PerformanceModeCheckbox") as CheckBox
            ?? throw new InvalidOperationException("PerformanceModeCheckbox was not found.");
        var windowInspection = window.FindName("WindowInspectionCheckbox") as CheckBox
            ?? throw new InvalidOperationException("WindowInspectionCheckbox was not found.");
        var size = window.FindName("MascotSizeSlider") as Slider
            ?? throw new InvalidOperationException("MascotSizeSlider was not found.");
        var opacity = window.FindName("MascotOpacitySlider") as Slider
            ?? throw new InvalidOperationException("MascotOpacitySlider was not found.");
        var applyMascotSettings = window.FindName("ApplyMascotSettingsButton") as Button
            ?? throw new InvalidOperationException("ApplyMascotSettingsButton was not found.");
        var baseUrl = window.FindName("BaseUrlInput") as TextBox
            ?? throw new InvalidOperationException("BaseUrlInput was not found.");
        var model = window.FindName("ModelInput") as TextBox
            ?? throw new InvalidOperationException("ModelInput was not found.");
        var saveProvider = window.FindName("SaveProviderButton") as Button
            ?? throw new InvalidOperationException("SaveProviderButton was not found.");
        var providerStatus = window.FindName("ProviderActionStatus") as TextBlock
            ?? throw new InvalidOperationException("ProviderActionStatus was not found.");

        // Exercise the visible language toggle in both directions and verify a fresh settings read.
        AppRuntime.Settings.Language = "ar";
        languageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, languageButton));
        Assert.AreEqual("en", AppRuntime.Settings.Language, "The header language button should switch Arabic to English.");
        Assert.AreEqual("en", store.Load().Language, "The language change must survive reloading settings.json.");
        StringAssert.Contains(settingsTab.Header?.ToString() ?? string.Empty, "Settings",
            "Switching to English must update the settings tab label.");
        Assert.AreEqual("Layal", dockBrandTitle.Text);
        Assert.AreEqual("Your AI workspace", dockBrandSubtitle.Text);
        Assert.AreEqual("Your AI companion", dockFooterTitle.Text);
        Assert.AreEqual("Your chats and tools, together.", dockFooterDescription.Text);
        Assert.IsInstanceOfType(dockChat.Content, typeof(StackPanel),
            "Switching to English must preserve the modern Fluent icon-and-label navigation row.");

        languageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, languageButton));
        Assert.AreEqual("ar", AppRuntime.Settings.Language, "The header language button should switch English back to Arabic.");
        Assert.AreEqual("ar", store.Load().Language, "Switching the language back must also persist.");
        StringAssert.Contains(settingsTab.Header?.ToString() ?? string.Empty, "الإعدادات",
            "Switching back to Arabic must update the settings tab label.");
        Assert.AreEqual("ليال", dockBrandTitle.Text);
        Assert.AreEqual("مساحة عملك الذكية", dockBrandSubtitle.Text);
        Assert.AreEqual("مساعدك الذكي", dockFooterTitle.Text);
        Assert.AreEqual("محادثاتك وأدواتك في مكان واحد.", dockFooterDescription.Text);
        Assert.IsInstanceOfType(dockChat.Content, typeof(StackPanel),
            "Switching back to Arabic must preserve the modern Fluent icon-and-label navigation row.");

        // Navigation entry points must select the real corresponding tabs.
        goSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, goSettings));
        Assert.AreSame(settingsTab, tabs.SelectedItem, "The home settings shortcut must navigate to Settings.");
        goChat.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, goChat));
        Assert.AreSame(chatTab, tabs.SelectedItem, "The overview chat shortcut must navigate to Chat.");

        // The persistent left rail starts expanded, supports compact mode, and navigates to real pages.
        var previousPerformanceMode = AppRuntime.Settings.PerformanceModeEnabled;
        AppRuntime.Settings.PerformanceModeEnabled = true;
        var bottomDockField = typeof(MainWindow).GetField("_isBottomDockExpanded", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Navigation rail expansion state was not found.");
        var toggleHistory = typeof(MainWindow).GetMethod("SetChatHistoryPanelOpen", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Chat history panel method was not found.");
        var brandCopy = window.FindName("DockBrandCopy") as StackPanel
            ?? throw new InvalidOperationException("DockBrandCopy was not found.");
        var footerNote = window.FindName("DockFooterNote") as Border
            ?? throw new InvalidOperationException("DockFooterNote was not found.");
        var railShell = window.FindName("BottomDockShell") as Border
            ?? throw new InvalidOperationException("BottomDockShell was not found.");
        var navigationViewport = window.FindName("DockNavigationScrollViewer") as ScrollViewer
            ?? throw new InvalidOperationException("DockNavigationScrollViewer was not found.");
        var brandMark = window.FindName("DockBrandMark") as Border
            ?? throw new InvalidOperationException("DockBrandMark was not found.");
        var workspaceHeading = window.FindName("DockWorkspaceHeading") as TextBlock
            ?? throw new InvalidOperationException("DockWorkspaceHeading was not found.");
        var personalHeading = window.FindName("DockPersonalHeading") as TextBlock
            ?? throw new InvalidOperationException("DockPersonalHeading was not found.");
        var navigationSeparator = window.FindName("DockNavigationSeparator") as Separator
            ?? throw new InvalidOperationException("DockNavigationSeparator was not found.");
        var navigationButtons = new[]
        {
            window.FindName("DockChatButton") as Button,
            window.FindName("DockOverviewButton") as Button,
            window.FindName("DockTasksButton") as Button,
            window.FindName("DockSkillsButton") as Button,
            window.FindName("DockCamerasButton") as Button,
            window.FindName("DockMemoryButton") as Button,
            window.FindName("DockApiButton") as Button,
            window.FindName("DockSettingsButton") as Button
        };
        var suggestionPlan = window.FindName("ChatSuggestionPlanButton") as Button
            ?? throw new InvalidOperationException("ChatSuggestionPlanButton was not found.");
        var chatInput = window.FindName("ChatInput") as TextBox
            ?? throw new InvalidOperationException("ChatInput was not found.");
        try
        {
            Assert.IsTrue((bool)bottomDockField.GetValue(window)!,
                "The navigation rail must start expanded so destinations are immediately visible.");
            Assert.AreEqual(166, expandedDock.Width, 0.1,
                "The expanded navigation rail must expose its labeled destinations.");
            Assert.AreEqual(Visibility.Visible, brandCopy.Visibility,
                "The expanded navigation rail must show its brand label.");
            Assert.AreEqual(Visibility.Visible, footerNote.Visibility,
                "The expanded navigation rail must show its footer note.");
            Assert.AreEqual(Visibility.Visible, brandMark.Visibility);
            Assert.AreEqual(Visibility.Visible, workspaceHeading.Visibility);
            Assert.AreEqual(Visibility.Visible, personalHeading.Visibility);
            Assert.AreEqual(Visibility.Visible, navigationSeparator.Visibility);
            Assert.AreEqual(188, railShell.ActualWidth, 0.5,
                "The expanded rail shell must equal its 166-DIP content body plus 22 DIP of padding and border.");
            Assert.AreEqual(166, navigationViewport.Width, 0.1);
            Assert.AreEqual(ScrollBarVisibility.Auto, navigationViewport.VerticalScrollBarVisibility);
            foreach (var navigationButton in navigationButtons)
            {
                Assert.IsNotNull(navigationButton);
                Assert.AreEqual(152, navigationButton!.Width, 0.1,
                    "Expanded navigation destinations must use the full 152-DIP row width.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(navigationButton)),
                    "Every navigation destination must retain a screen-reader name.");
                var row = navigationButton.Content as StackPanel;
                Assert.IsNotNull(row, "Expanded destinations must combine a Fluent icon tile and a readable label.");
                Assert.IsInstanceOfType(row!.Children[0], typeof(Border));
                var iconTile = (Border)row.Children[0];
                Assert.AreEqual(10, iconTile.CornerRadius.TopLeft, 0.1);
                var iconGlyph = iconTile.Child as TextBlock;
                Assert.IsNotNull(iconGlyph);
                StringAssert.Contains(iconGlyph!.FontFamily.Source, "Segoe Fluent Icons");
            }

            dockToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, dockToggle));
            Assert.IsFalse((bool)bottomDockField.GetValue(window)!,
                "The navigation rail toggle must enter compact mode.");
            Assert.AreEqual(48, expandedDock.Width, 0.1,
                "The compact navigation rail must retain its icon destinations.");
            Assert.AreEqual(Visibility.Collapsed, brandCopy.Visibility,
                "The compact navigation rail must hide its brand label.");
            Assert.AreEqual(Visibility.Collapsed, footerNote.Visibility,
                "The compact navigation rail must hide its footer note.");
            Assert.AreEqual(Visibility.Collapsed, brandMark.Visibility,
                "The compact rail must hide the brand mark so it cannot force the shell wider than its icon rail.");
            Assert.AreEqual(Visibility.Collapsed, workspaceHeading.Visibility);
            Assert.AreEqual(Visibility.Collapsed, personalHeading.Visibility);
            Assert.AreEqual(Visibility.Collapsed, navigationSeparator.Visibility);
            window.UpdateLayout();
            Assert.AreEqual(70, railShell.ActualWidth, 0.5,
                "The compact rail shell must equal its 48-DIP icon body plus 22 DIP of padding and border.");
            Assert.AreEqual(48, navigationViewport.Width, 0.1);
            Assert.AreEqual(ScrollBarVisibility.Hidden, navigationViewport.VerticalScrollBarVisibility,
                "The compact rail must hide its vertical scrollbar so 44-DIP icons retain their full viewport width.");
            foreach (var navigationButton in navigationButtons)
            {
                Assert.IsNotNull(navigationButton);
                Assert.AreEqual(44, navigationButton!.Width, 0.1,
                    "Compact navigation destinations must use 44-DIP icon-only targets.");
                Assert.IsTrue(navigationButton.MinHeight >= 44,
                    "Compact navigation destinations must preserve a 44-DIP minimum touch target.");
                var iconTile = navigationButton.Content as Border;
                Assert.IsNotNull(iconTile, "Compact navigation destinations must retain their colored icon tiles.");
                Assert.IsInstanceOfType(iconTile!.Child, typeof(TextBlock));
                var iconGlyph = (TextBlock)iconTile.Child;
                Assert.IsFalse(string.IsNullOrWhiteSpace(iconGlyph.Text),
                    "Compact navigation buttons must show only their icon, never clipped labels.");
                Assert.IsTrue(iconTile.Background is SolidColorBrush,
                    "Each compact Fluent icon must retain a coordinated theme-aware color tile.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(navigationButton.ToolTip?.ToString()),
                    "Every icon-only destination must provide a localized tooltip.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(navigationButton)),
                    "Every icon-only destination must retain an accessible localized name.");
            }
            AssertNavigationRailDoesNotOverlapContent(window, "collapsed navigation rail");

            dockToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, dockToggle));
            Assert.IsTrue((bool)bottomDockField.GetValue(window)!,
                "The navigation rail toggle must restore the expanded layout.");
            Assert.AreEqual(166, expandedDock.Width, 0.1,
                "The expanded rail must restore the full navigation width.");
            window.UpdateLayout();
            Assert.AreEqual(188, railShell.ActualWidth, 0.5);
            Assert.AreEqual(Visibility.Visible, brandMark.Visibility);
            Assert.AreEqual(Visibility.Visible, workspaceHeading.Visibility);
            Assert.AreEqual(Visibility.Visible, personalHeading.Visibility);
            Assert.AreEqual(Visibility.Visible, navigationSeparator.Visibility);
            foreach (var navigationButton in navigationButtons)
                Assert.AreEqual(152, navigationButton!.Width, 0.1);
            AssertNavigationRailDoesNotOverlapContent(window, "restored expanded navigation rail");
            dockChat.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, dockChat));
            Assert.AreSame(chatTab, tabs.SelectedItem, "The first navigation destination must return to the primary chat page.");
            dockOverview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, dockOverview));
            Assert.AreSame(homeTab, tabs.SelectedItem, "The overview navigation destination must open the dashboard.");
            Assert.IsTrue((bool)bottomDockField.GetValue(window)!,
                "Page navigation must not unexpectedly collapse the left navigation rail.");
            Assert.AreEqual(166, expandedDock.Width, 0.1);

            // Prompt starters must provide usable, localized text in the composer.
            suggestionPlan.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, suggestionPlan));
            StringAssert.Contains(chatInput.Text, "خطة واضحة");
            Assert.AreEqual(chatInput.Text.Length, chatInput.CaretIndex);
            chatInput.Clear();

            Assert.AreEqual(Visibility.Collapsed, historyPanel.Visibility,
                "Saved conversation history must not steal space from the chat until requested.");
            historyToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, historyToggle));
            Assert.AreEqual(Visibility.Visible, historyPanel.Visibility,
                "The chat history toggle must show the collapsible side panel.");
            toggleHistory.Invoke(window, new object[] { false });
            Assert.AreEqual(Visibility.Collapsed, historyPanel.Visibility,
                "Closing the history panel must return the full-width chat surface.");
        }
        finally
        {
            AppRuntime.Settings.PerformanceModeEnabled = previousPerformanceMode;
            tabs.SelectedItem = homeTab;
        }

        // Checkbox changes persist immediately and are reflected by the live mascot.
        alwaysOnTop.IsChecked = false;
        Assert.IsFalse(store.Load().MascotAlwaysOnTop, "Turning off always-on-top must persist immediately.");
        Assert.IsFalse(mascot.Topmost, "The live mascot must stop staying above other windows.");
        alwaysOnTop.IsChecked = true;
        Assert.IsTrue(store.Load().MascotAlwaysOnTop, "Turning always-on-top back on must persist.");
        Assert.IsTrue(mascot.Topmost, "The live mascot must return to topmost mode.");

        animations.IsChecked = false;
        Assert.IsFalse(store.Load().MascotAnimationsEnabled, "Disabling mascot animation must persist immediately.");
        animations.IsChecked = true;
        Assert.IsTrue(store.Load().MascotAnimationsEnabled, "Re-enabling mascot animation must persist.");

        // Performance mode is an effective override only; it must not rewrite the user's
        // animation preference, and the live hit-test budget must switch between both rates.
        var hitTestTimerField = typeof(FloatingMascotWindow).GetField("_hitTestTimer", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Mascot hit-test timer was not found.");
        var hitTestTimer = hitTestTimerField.GetValue(mascot) as DispatcherTimer
            ?? throw new InvalidOperationException("Mascot hit-test timer is not initialized.");
        performanceMode.IsChecked = true;
        var performanceSettings = store.Load();
        Assert.IsTrue(performanceSettings.PerformanceModeEnabled, "Enabling performance mode must persist.");
        Assert.IsTrue(performanceSettings.MascotAnimationsEnabled, "Performance mode must preserve the normal animation preference.");
        Assert.AreEqual(33, hitTestTimer.Interval.TotalMilliseconds, 0.1,
            "Performance mode should reduce high-frequency hit testing to about 30 Hz.");
        performanceMode.IsChecked = false;
        var normalSettings = store.Load();
        Assert.IsFalse(normalSettings.PerformanceModeEnabled, "Disabling performance mode must persist.");
        Assert.IsTrue(normalSettings.MascotAnimationsEnabled, "Disabling performance mode should restore the previously enabled animation preference.");
        Assert.AreEqual(8, hitTestTimer.Interval.TotalMilliseconds, 0.1,
            "Disabling performance mode should restore the normal hit-test interval.");

        // The window-title permission is opt-in and must round-trip in both directions.
        windowInspection.IsChecked = true;
        Assert.IsTrue(store.Load().AllowWindowTitleInspection, "Enabling optional window-title inspection must persist.");
        windowInspection.IsChecked = false;
        Assert.IsFalse(store.Load().AllowWindowTitleInspection, "Disabling optional window-title inspection must persist.");

        // Apply the size/opacity controls through the actual XAML Click event, then reload settings.
        size.Value = 1.25;
        opacity.Value = 0.8;
        applyMascotSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, applyMascotSettings));
        var applied = store.Load();
        Assert.AreEqual(1.25, applied.MascotScale, 0.0001, "Mascot scale must persist after Apply.");
        Assert.AreEqual(0.8, applied.MascotOpacity, 0.0001, "Mascot opacity must persist after Apply.");
        Assert.IsTrue(applied.MascotAlwaysOnTop, "Always-on-top state must persist alongside slider settings.");
        Assert.IsTrue(mascot.Topmost, "Apply must keep the live window's topmost state in sync.");
        Assert.AreEqual(0.8, mascot.Opacity, 0.001, "Apply must update live window opacity.");
        Assert.AreEqual(248 * 1.25, mascot.Width, 0.5, "Apply must resize the live mascot width within WPF device-pixel rounding tolerance.");
        Assert.AreEqual(175 * 1.25, mascot.Height, 0.5, "Apply must resize the live mascot height within WPF layout rounding tolerance.");

        // The provider form must reject an unsupported scheme without partially saving it.
        var oldBaseUrl = applied.ProviderBaseUrl;
        var oldModelId = applied.ModelId;
        baseUrl.Text = "file:///not-a-provider";
        model.Text = "should-not-be-saved";
        saveProvider.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, saveProvider));
        Assert.AreEqual(oldBaseUrl, store.Load().ProviderBaseUrl, "An invalid provider URL must not overwrite a previously saved URL.");
        Assert.AreEqual(oldModelId, store.Load().ModelId, "A failed provider save must not partially write the model ID.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(providerStatus.Text), "Invalid provider settings must show feedback.");

        // A valid URL/model pair must be normalized and persisted without requiring network access.
        baseUrl.Text = "http://127.0.0.1:31415/v1/";
        model.Text = "interaction-regression-test";
        saveProvider.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, saveProvider));
        Assert.AreEqual("http://127.0.0.1:31415/v1", store.Load().ProviderBaseUrl);
        Assert.AreEqual("interaction-regression-test", store.Load().ModelId);

        // Restore the exact original provider state. The form intentionally rejects an empty
        // URL/model pair, so returning a fresh install to its unconfigured state must bypass UI validation.
        baseUrl.Text = oldBaseUrl;
        model.Text = oldModelId;
        var restoredSettings = store.Load();
        restoredSettings.ProviderBaseUrl = oldBaseUrl;
        restoredSettings.ModelId = oldModelId;
        store.Save(restoredSettings);
        AppRuntime.Settings = restoredSettings;
        alwaysOnTop.IsChecked = true;
        animations.IsChecked = true;
        windowInspection.IsChecked = false;
        size.Value = 1.0;
        opacity.Value = 1.0;
        applyMascotSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, applyMascotSettings));
        Assert.IsTrue(store.Load().MascotAlwaysOnTop);
        Assert.IsTrue(store.Load().MascotAnimationsEnabled);
        Assert.AreEqual(1.0, store.Load().MascotScale, 0.0001);
        Assert.AreEqual(1.0, store.Load().MascotOpacity, 0.0001);
        Assert.IsFalse(store.Load().AllowWindowTitleInspection);
        Assert.AreEqual(oldBaseUrl, store.Load().ProviderBaseUrl);
        Assert.AreEqual(oldModelId, store.Load().ModelId);

        // Test the show control against the real floating window, then leave the harness in a safe state.
        mascot.Hide();
        showMascot.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, showMascot));
        Assert.IsTrue(mascot.IsVisible, "The show-mascot button must restore a hidden mascot window.");
        Assert.IsTrue(mascot.Topmost, "ShowAgain must respect the persisted always-on-top preference.");

        AppRuntime.Settings = store.Load();
        AppRuntime.Settings.ThemeMode = "light";
        AppRuntime.Settings.Language = "ar";
        store.Save(AppRuntime.Settings);
        var applyTheme = typeof(MainWindow).GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyTheme was not found.");
        var applyLanguage = typeof(MainWindow).GetMethod("ApplyLanguage", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyLanguage was not found.");
        applyTheme.Invoke(window, null);
        applyLanguage.Invoke(window, null);
        mascot.UpdateLanguage();
        mascot.Topmost = AppRuntime.Settings.MascotAlwaysOnTop;
        mascot.Opacity = AppRuntime.Settings.MascotOpacity;
        mascot.ApplyScale(AppRuntime.Settings.MascotScale);
    }

    private static void AssertThemeContrastAndControlSurfaces(
        string theme,
        string language,
        ComboBox historySelector)
    {
        var resources = Application.Current.Resources;
        var background = (Color)resources["Layal.Color.Background"];
        var text = (Color)resources["Layal.Color.Text"];
        var muted = (Color)resources["Layal.Color.Muted"];
        var scrollThumb = (Color)resources["Layal.Color.ScrollThumb"];
        var panelBrush = Application.Current.TryFindResource("Layal.Panel") as SolidColorBrush;
        var comboBackground = historySelector.Background as SolidColorBrush;
        var textBrush = Application.Current.TryFindResource("Layal.Text") as SolidColorBrush;
        var comboForeground = historySelector.Foreground as SolidColorBrush;

        foreach (var popupType in new[] { typeof(ContextMenu), typeof(MenuItem), typeof(ToolTip), typeof(DatePicker) })
        {
            Assert.IsNotNull(Application.Current.TryFindResource(popupType) as Style,
                $"Implicit {popupType.Name} style must be available in {theme}/{language}; transient surfaces must not fall back to native white styling.");
        }

        var calendarStyle = Application.Current.TryFindResource("Layal.CalendarPopup") as Style;
        Assert.IsNotNull(calendarStyle, $"DatePicker calendar popup style must be available in {theme}/{language}.");
        Assert.IsTrue(calendarStyle!.Setters.OfType<Setter>()
                .Any(setter => setter.Property == Control.BackgroundProperty),
            $"DatePicker calendar popup must have a theme-aware background in {theme}/{language}.");

        foreach (var calendarControlType in new[] { typeof(CalendarDayButton), typeof(CalendarButton), typeof(DatePickerTextBox) })
        {
            Assert.IsNotNull(Application.Current.TryFindResource(calendarControlType) as Style,
                $"The DatePicker popup sub-control '{calendarControlType.Name}' must use Layal's theme resources in {theme}/{language}.");
        }
        var calendarDayStyle = (Style)Application.Current.TryFindResource(typeof(CalendarDayButton))!;
        Assert.IsTrue(calendarDayStyle.Triggers.OfType<Trigger>()
                .Any(trigger => trigger.Property == CalendarDayButton.IsSelectedProperty),
            "Calendar day selection must have a dedicated visible selected state.");
        Assert.IsTrue(calendarDayStyle.Triggers.OfType<Trigger>()
                .Any(trigger => trigger.Property == CalendarDayButton.IsBlackedOutProperty),
            "Unavailable calendar days must have a distinct disabled appearance.");

        var glassPopupBrush = Application.Current.TryFindResource("Layal.GlassStrongFill") as SolidColorBrush;
        var glassBorderBrush = Application.Current.TryFindResource("Layal.GlassBorderBrush") as SolidColorBrush;
        var menuHoverBrush = Application.Current.TryFindResource("Layal.MenuHover") as SolidColorBrush;
        var menuHoverTextBrush = Application.Current.TryFindResource("Layal.MenuHoverText") as SolidColorBrush;
        Assert.IsNotNull(glassPopupBrush, $"Glass popup fill must resolve in {theme}/{language}.");
        Assert.IsNotNull(glassBorderBrush, $"Glass popup border must resolve in {theme}/{language}.");
        Assert.IsNotNull(menuHoverBrush, $"Menu selection background must resolve in {theme}/{language}.");
        Assert.IsNotNull(menuHoverTextBrush, $"Menu selection text must resolve in {theme}/{language}.");

        var highContrastPalette = UiThemePalette.Create(dark: theme == "dark", highContrast: true);
        var expectedMenuHighlight = SystemColors.HighlightColor;
        var expectedMenuHighlightText = SystemColors.HighlightTextColor;
        Assert.AreEqual(expectedMenuHighlight, (Color)ColorConverter.ConvertFromString(highContrastPalette["Layal.Color.MenuHover"])!,
            "High Contrast menu selection must use the active Windows highlight color.");
        Assert.AreEqual(expectedMenuHighlightText, (Color)ColorConverter.ConvertFromString(highContrastPalette["Layal.Color.MenuHoverText"])!,
            "High Contrast selected menu text must use the Windows highlight text color.");

        Assert.IsNotNull(panelBrush, $"Layal.Panel must resolve for {theme}/{language}.");
        Assert.IsNotNull(comboBackground, $"ComboBox background must use a solid theme surface for {theme}/{language}.");
        Assert.IsNotNull(textBrush, $"Layal.Text must resolve for {theme}/{language}.");
        Assert.IsNotNull(comboForeground, $"ComboBox text must use a theme-aware foreground for {theme}/{language}.");
        Assert.AreEqual(panelBrush.Color, comboBackground.Color,
            $"Chat history ComboBox leaked a native/default surface in {theme}/{language}.");
        Assert.AreEqual(textBrush.Color, comboForeground.Color,
            $"Chat history ComboBox text does not follow {theme}/{language} text resources.");

        AssertTransientPopupOpenClose(
            Window.GetWindow(historySelector)
                ?? throw new InvalidOperationException("Chat history selector is not attached to a window."),
            theme, language);

        Assert.IsTrue(ContrastRatio(text, background) >= 7.0,
            $"Primary text contrast must be at least 7:1 in {theme}/{language}.");
        Assert.IsTrue(ContrastRatio(muted, background) >= 4.5,
            $"Muted text contrast must be at least 4.5:1 in {theme}/{language}.");
        Assert.IsTrue(ContrastRatio(scrollThumb, background) >= 2.0,
            $"Scrollbar thumb contrast must be at least 2:1 in {theme}/{language}.");
    }

    private static void AssertTransientPopupOpenClose(
        Window owner,
        string theme,
        string language)
    {
        var glassFill = Application.Current.TryFindResource("Layal.GlassStrongFill") as SolidColorBrush
            ?? throw new InvalidOperationException("The glass popup fill must resolve before transient controls are opened.");
        var dispatcher = owner.Dispatcher;

        var contextTarget = new Button
        {
            Content = language == "ar" ? "هدف القائمة" : "Popup target",
            MinWidth = 120,
            MinHeight = 36
        };
        var comboProbe = new ComboBox { Width = 220, MinHeight = 36 };
        comboProbe.Items.Add(new ComboBoxItem { Content = language == "ar" ? "اختيار أول" : "First option" });
        comboProbe.Items.Add(new ComboBoxItem { Content = language == "ar" ? "اختيار ثانٍ" : "Second option" });
        comboProbe.SelectedIndex = 0;
        var datePicker = new DatePicker { SelectedDate = new DateTime(2026, 5, 12), Width = 260 };

        var probeHost = new Window
        {
            Owner = owner,
            Width = 330,
            Height = 230,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            ShowActivated = true,
            ResizeMode = ResizeMode.NoResize,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Orientation = Orientation.Vertical,
                Children = { contextTarget, comboProbe, datePicker }
            }
        };

        try
        {
            probeHost.Show();
            probeHost.UpdateLayout();
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var contextMenu = new ContextMenu
            {
                PlacementTarget = contextTarget,
                Placement = PlacementMode.Bottom
            };
            contextMenu.Items.Add(new MenuItem
            {
                Header = language == "ar" ? "عنصر قائمة تجريبي" : "Transient menu item"
            });
            try
            {
                contextMenu.IsOpen = true;
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.IsTrue(contextMenu.IsOpen, $"ContextMenu must open in {theme}/{language}.");
                Assert.IsNotNull(contextMenu.Template, "The context menu must resolve a WPF control template.");
                Assert.AreEqual(glassFill.Color,
                    (contextMenu.Background as SolidColorBrush)?.Color,
                    $"ContextMenu must use the glass fill in {theme}/{language}.");
            }
            finally
            {
                contextMenu.IsOpen = false;
            }

            var toolTip = new ToolTip
            {
                PlacementTarget = contextTarget,
                Content = language == "ar" ? "تلميح تجريبي" : "Transient tooltip"
            };
            try
            {
                toolTip.IsOpen = true;
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.IsTrue(toolTip.IsOpen, $"ToolTip must open in {theme}/{language}.");
                Assert.AreEqual(glassFill.Color,
                    (toolTip.Background as SolidColorBrush)?.Color,
                    $"ToolTip must use the glass fill in {theme}/{language}.");
            }
            finally
            {
                toolTip.IsOpen = false;
            }

            comboProbe.ApplyTemplate();
            var comboPopup = comboProbe.Template.FindName("PART_Popup", comboProbe) as Popup
                ?? throw new InvalidOperationException("The themed ComboBox template must expose PART_Popup.");
            comboProbe.IsDropDownOpen = true;
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.IsTrue(comboPopup.IsOpen, $"ComboBox popup must open in {theme}/{language}.");
            var popupSurface = comboPopup.Child as Border
                ?? throw new InvalidOperationException("The ComboBox popup must render through its glass Border surface.");
            Assert.AreEqual(glassFill.Color,
                (popupSurface.Background as SolidColorBrush)?.Color,
                $"ComboBox popup must use the glass fill in {theme}/{language}.");
            comboProbe.IsDropDownOpen = false;

            datePicker.ApplyTemplate();
            var datePopup = datePicker.Template.FindName("PART_Popup", datePicker) as Popup
                ?? throw new InvalidOperationException("The DatePicker template must expose PART_Popup.");
            datePicker.IsDropDownOpen = true;
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.IsTrue(datePopup.IsOpen, $"DatePicker calendar popup must open in {theme}/{language}.");
            Assert.IsNotNull(datePopup.Child, "The open DatePicker popup must contain its calendar surface.");
            Assert.IsNotNull(datePicker.CalendarStyle,
                "The DatePicker must pass its theme-aware Calendar style into the popup.");
            Assert.AreSame(Application.Current.TryFindResource("Layal.CalendarPopup"), datePicker.CalendarStyle,
                $"The open DatePicker popup must use the shared calendar style in {theme}/{language}.");
            datePicker.IsDropDownOpen = false;
        }
        finally
        {
            probeHost.Close();
        }
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) +
               0.7152 * Linear(color.G) +
               0.0722 * Linear(color.B);
    }

    private static void InitializeRuntime(string root)
    {
        AppRuntime.SettingsStore = new SettingsStore(root);
        AppRuntime.Settings = new UserSettings();
        AppRuntime.SecretStore = new DpapiSecretStore(Path.Combine(root, "secrets", "visual-smoke.dpapi"));
        AppRuntime.ApiConnectionService = new ApiConnectionService(Path.Combine(root, "api-connections.dpapi"));
        AppRuntime.SkillPackageService = new SkillPackageService(Path.Combine(root, "skills"));
        AppRuntime.MemoryService = new MemoryService(Path.Combine(root, "memory.dpapi"));
        AppRuntime.TaskHistoryService = new TaskHistoryService(Path.Combine(root, "tasks.dpapi"));
        AppRuntime.ReminderService = new ReminderService(Path.Combine(root, "reminders.dpapi"));
        AppRuntime.SavedRoutineService = new SavedRoutineService(Path.Combine(root, "saved-routines.dpapi"));
        AppRuntime.ChatHistoryStore = new ProtectedJsonStore<List<ChatMessage>>(Path.Combine(root, "chat.dpapi"));
        AppRuntime.ChatSessionService = new ChatSessionService(
            Path.Combine(root, "sessions.dpapi"), AppRuntime.ChatHistoryStore, "Visual smoke");
        AppRuntime.WorkspaceRoot = Path.Combine(root, "workspace");
        AppRuntime.AgentToolExecutor = new AgentToolExecutor(AppRuntime.WorkspaceRoot, AppRuntime.MemoryService);
        AppRuntime.AgentOrchestrator = new AgentOrchestrator(
            new OpenAiCompatibleClient(), AppRuntime.AgentToolExecutor,
            AppRuntime.SkillPackageService, AppRuntime.TaskHistoryService);
        AppRuntime.IsExiting = false;
        AppRuntime.IsPrivateChatActive = false;
    }

    private static void SaveSnapshot(
        FrameworkElement visual,
        string path,
        double renderScale = 1.0,
        Brush? backdrop = null)
    {
        visual.UpdateLayout();
        renderScale = Math.Clamp(renderScale, 1.0, 2.0);
        var dpi = 96.0 * renderScale;
        var widthDips = Math.Max(1, visual.ActualWidth);
        var heightDips = Math.Max(1, visual.ActualHeight);
        var width = Math.Max(1, (int)Math.Ceiling(widthDips * renderScale));
        var height = Math.Max(1, (int)Math.Ceiling(heightDips * renderScale));

        Visual renderSource = visual;
        if (backdrop is not null)
        {
            // WPF's Window/root content can include transparent margins and shadow pixels.
            // Composite the actual theme background behind the visual to make the PNG match
            // how those same pixels appear inside the live window instead of viewer black/blue.
            var composite = new DrawingVisual();
            using (var drawing = composite.RenderOpen())
            {
                var bounds = new Rect(0, 0, widthDips, heightDips);
                drawing.DrawRectangle(backdrop, null, bounds);
                drawing.DrawRectangle(
                    new VisualBrush(visual) { Stretch = Stretch.Fill },
                    null,
                    bounds);
            }

            renderSource = composite;
        }

        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(renderSource);
        if (bitmap.PixelWidth < 1 || bitmap.PixelHeight < 1)
            throw new InvalidOperationException($"Empty snapshot generated for {visual.GetType().Name}.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
