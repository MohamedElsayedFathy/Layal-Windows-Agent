using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Layal.Models;
using Layal.Services;

namespace Layal.Views;

public partial class MainWindow
{
    public void CancelActiveOperationsForShutdown()
    {
        _weatherCancellation?.Cancel();
        _speechRecordingTimer.Stop();
        _speechCancellation?.Cancel();
        _voiceTestCancellation?.Cancel();
        _localAiSetupCancellation?.Cancel();
        _chatCancellation?.Cancel();
        _agentTaskCancellation?.Cancel();
        _voiceConversationCancellation?.Cancel();

        if (_localVoiceRecordingActive)
            CancelLocalVoiceRecording();
        else if (!_speechIsStopping)
        {
            try { _microphoneCapture.CancelRecording(); }
            catch { /* Shutdown should continue even if capture already stopped. */ }
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _hoveredElementInspectionCancellation?.Cancel();
        _documentExtractionCancellation?.Cancel();
        _speechRecordingTimer.Stop();
        _speechCancellation?.Cancel();
        _voiceTestCancellation?.Cancel();
        _localAiSetupCancellation?.Cancel();
        if (_localVoiceRecordingActive) CancelLocalVoiceRecording();
        _voiceConversationCancellation?.Cancel();
        if (!_speechIsStopping) _microphoneCapture.CancelRecording();

        if (!AppRuntime.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            _chatCancellation?.Cancel();
            _agentTaskCancellation?.Cancel();
        }
    }
}
