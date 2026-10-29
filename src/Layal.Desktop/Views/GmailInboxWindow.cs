using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Layal.Models;
using Layal.Services;

namespace Layal.Views;

/// <summary>Local-only Gmail inbox preview with explicit consent before copying selected text to chat.</summary>
public sealed class GmailInboxWindow : Window
{
    private readonly GmailReadOnlyService _service;
    private readonly bool _isArabic;
    private readonly Action<string> _saveClientId;
    private readonly Action<string> _addToChatDraft;
    private readonly ObservableCollection<GmailMessageSummary> _messages = new();
    private readonly TextBox _clientId;
    private readonly TextBox _query;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly TextBlock _subject;
    private readonly TextBlock _metadata;
    private readonly TextBox _body;
    private readonly Button _connect;
    private readonly Button _refresh;
    private readonly Button _disconnect;
    private readonly Button _search;
    private readonly Button _addToDraft;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _readOperation;
    private GmailMessageDetail? _selectedDetail;

    public GmailInboxWindow(
        GmailReadOnlyService service,
        string clientId,
        bool isArabic,
        Action<string> saveClientId,
        Action<string> addToChatDraft)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _isArabic = isArabic;
        _saveClientId = saveClientId ?? throw new ArgumentNullException(nameof(saveClientId));
        _addToChatDraft = addToChatDraft ?? throw new ArgumentNullException(nameof(addToChatDraft));

        Title = isArabic ? "Gmail — قراءة فقط" : "Gmail — read only";
        Width = 1050; Height = 720; MinWidth = 760; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Application.Current.TryFindResource("Layal.Background") as Brush ?? Brushes.White;
        Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black;
        AutomationProperties.SetName(this, Title);
        AutomationProperties.SetHelpText(this, isArabic
            ? "صلاحية Gmail للقراءة فقط. الرسائل تبقى محلية ولا تُرسل إلى AI تلقائيًا."
            : "Read-only Gmail access. Messages stay local and are never automatically sent to AI.");

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        heading.Children.Add(new TextBlock { Text = isArabic ? "البريد الإلكتروني — قراءة فقط" : "Email — read only", FontSize = 21, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = isArabic
                ? "يتطلب Google OAuth Desktop Client. تطلب ليال gmail.readonly فقط؛ لا إرسال أو حذف أو تعديل. لا تتم مشاركة محتوى البريد مع AI إلا إذا اخترت إضافته إلى مسودة الشات ثم ضغطت إرسال."
                : "Requires a Google OAuth Desktop client. Layal requests gmail.readonly only; it cannot send, delete, or modify mail. Email content is not shared with AI unless you copy it into the chat draft and press Send.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray
        });
        Grid.SetRow(heading, 0); root.Children.Add(heading);

        var credentials = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        credentials.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        credentials.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        credentials.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        credentials.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var clientPanel = new StackPanel();
        clientPanel.Children.Add(new TextBlock { Text = "Google OAuth Desktop Client ID" });
        _clientId = new TextBox { Text = clientId ?? string.Empty, MinHeight = 32, Margin = new Thickness(0, 3, 8, 0), FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetName(_clientId, "Google OAuth Desktop Client ID");
        AutomationProperties.SetHelpText(_clientId, "Public client ID ending with .apps.googleusercontent.com. Do not enter a client secret or API key.");
        clientPanel.Children.Add(_clientId);
        Grid.SetColumn(clientPanel, 0); credentials.Children.Add(clientPanel);

        _connect = MakeButton(isArabic ? "ربط Gmail" : "Connect Gmail", isArabic ? "ربط Gmail للقراءة فقط" : "Connect Gmail read-only", Connect_Click);
        Grid.SetColumn(_connect, 1); credentials.Children.Add(_connect);
        _refresh = MakeButton(isArabic ? "تحديث" : "Refresh", isArabic ? "تحديث الرسائل" : "Refresh messages", Refresh_Click);
        Grid.SetColumn(_refresh, 2); credentials.Children.Add(_refresh);
        _disconnect = MakeButton(isArabic ? "فصل محلي" : "Disconnect", isArabic ? "حذف الرمز من هذا الجهاز" : "Delete local token", Disconnect_Click);
        AutomationProperties.SetHelpText(_disconnect, isArabic
            ? "يحذف الرمز من هذا الجهاز. لإلغاء التفويض لدى Google أيضًا، احذف التطبيق من إعدادات أمان حساب Google."
            : "Deletes the token from this device. Revoke the Google grant too from Google Account security settings.");
        Grid.SetColumn(_disconnect, 3); credentials.Children.Add(_disconnect);
        Grid.SetRow(credentials, 1); root.Children.Add(credentials);

        var searchPanel = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        searchPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _query = new TextBox { Text = "newer_than:30d", MinHeight = 32, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(_query, isArabic ? "عبارة بحث Gmail" : "Gmail search query");
        AutomationProperties.SetHelpText(_query, isArabic
            ? "استخدم صيغة Gmail مثل from: أو subject: أو newer_than:7d. هذا بحث فقط ولا يغيّر الرسائل."
            : "Use Gmail search syntax such as from:, subject:, or newer_than:7d. Search only; messages are not modified.");
        searchPanel.Children.Add(_query);
        _search = MakeButton(isArabic ? "بحث" : "Search", isArabic ? "البحث في Gmail" : "Search Gmail", Refresh_Click);
        Grid.SetColumn(_search, 1); searchPanel.Children.Add(_search);
        Grid.SetRow(searchPanel, 2); root.Children.Add(searchPanel);

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        _list = new ListBox { ItemsSource = _messages, DisplayMemberPath = nameof(GmailMessageSummary.Subject), Padding = new Thickness(5),
            Background = Application.Current.TryFindResource("Layal.Panel") as Brush ?? Brushes.White,
            Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black,
            BorderBrush = Application.Current.TryFindResource("Layal.Border") as Brush ?? Brushes.Gray };
        AutomationProperties.SetName(_list, isArabic ? "قائمة رسائل Gmail" : "Gmail message list");
        AutomationProperties.SetHelpText(_list, isArabic ? "اختر رسالة لمعاينتها محليًا؛ لا تُرسل إلى AI." : "Select an email for a local preview; it is not sent to AI.");
        _list.SelectionChanged += MessageList_SelectionChanged;
        split.Children.Add(_list);

        var preview = new Grid { Margin = new Thickness(12, 0, 0, 0) };
        preview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        preview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        preview.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _subject = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(_subject, isArabic ? "عنوان البريد" : "Email subject");
        Grid.SetRow(_subject, 0); preview.Children.Add(_subject);
        _metadata = new TextBlock { Margin = new Thickness(0, 5, 0, 8), TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray };
        AutomationProperties.SetName(_metadata, isArabic ? "بيانات البريد" : "Email metadata");
        Grid.SetRow(_metadata, 1); preview.Children.Add(_metadata);
        _body = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8),
            Background = Application.Current.TryFindResource("Layal.Panel") as Brush ?? Brushes.White,
            Foreground = Application.Current.TryFindResource("Layal.Text") as Brush ?? Brushes.Black };
        AutomationProperties.SetName(_body, isArabic ? "معاينة نص البريد" : "Email text preview");
        AutomationProperties.SetHelpText(_body, isArabic
            ? "نص text/plain محلي بحد 15000 حرف؛ المرفقات وHTML فقط غير معروضين."
            : "Local text/plain body, capped at 15,000 characters; attachments and HTML-only content are not rendered.");
        Grid.SetRow(_body, 2); preview.Children.Add(_body);
        Grid.SetColumn(preview, 1); split.Children.Add(preview);
        Grid.SetRow(split, 3); root.Children.Add(split);

        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Application.Current.TryFindResource("Layal.Muted") as Brush ?? Brushes.DimGray };
        AutomationProperties.SetName(_status, isArabic ? "حالة Gmail" : "Gmail status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        footer.Children.Add(_status);
        _addToDraft = MakeButton(isArabic ? "إضافة إلى مسودة الشات" : "Add to chat draft",
            isArabic ? "إضافة البريد إلى مسودة الشات بعد التأكيد" : "Add selected email to chat draft after confirmation", AddToDraft_Click);
        DockPanel.SetDock(_addToDraft, Dock.Right); footer.Children.Add(_addToDraft);
        Grid.SetRow(footer, 4); root.Children.Add(footer);
        Content = root;

        _status.Text = _service.IsConnected
            ? (isArabic ? "متصل للقراءة فقط. اضغط تحديث لعرض الرسائل." : "Connected read-only. Press Refresh to list messages.")
            : (isArabic ? "أدخل Client ID من Google Cloud ثم اضغط ربط Gmail." : "Enter a Google Cloud client ID, then press Connect Gmail.");
        UpdateConnectionControls();
    }

    private Button MakeButton(string content, string name, RoutedEventHandler handler)
    {
        var button = new Button { Content = content, MinHeight = 34, MinWidth = 100, Margin = new Thickness(4, 20, 4, 0) };
        AutomationProperties.SetName(button, name);
        button.Click += handler;
        return button;
    }

    private CancellationTokenSource BeginOperation()
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        UpdateConnectionControls();
        _clientId.IsEnabled = false;
        _query.IsEnabled = false;
        _status.Text = _isArabic ? "جارٍ تنفيذ العملية..." : "Working…";
        return _operation;
    }

    private void EndOperation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_operation, cancellation))
            _operation = null;
        cancellation.Dispose();
        UpdateConnectionControls();
        _clientId.IsEnabled = true;
        _query.IsEnabled = true;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        string clientId;
        try { clientId = GmailReadOnlyService.ValidateOAuthClientId(_clientId.Text); }
        catch (Exception ex) { _status.Text = ex.Message; return; }

        var answer = MessageBox.Show(this,
            _isArabic
                ? "ستفتح صفحة Google الرسمية. ستطلب ليال gmail.readonly فقط. الرمز المتجدد سيُخزن مشفرًا محليًا. لا إرسال أو حذف أو تعديل للبريد، ولا مشاركة مع AI قبل إضافته لمسودة الشات ثم الضغط على إرسال. هل تريد المتابعة؟"
                : "Google's official sign-in page will open. Layal requests gmail.readonly only. The refresh token is stored locally encrypted. Email cannot be sent, deleted, or modified; content is not shared with AI until it is added to the chat draft and you press Send. Continue?",
            _isArabic ? "تأكيد ربط Gmail" : "Confirm Gmail connection",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        var cancellation = BeginOperation();
        try
        {
            _saveClientId(clientId);
            await _service.AuthorizeAsync(clientId, cancellation.Token);
            _status.Text = _isArabic ? "تم ربط Gmail. لم تُرسل أي رسالة إلى AI." : "Gmail connected. No email was sent to AI.";
            await LoadMessagesAsync(cancellation.Token);
        }
        catch (OperationCanceledException) { _status.Text = _isArabic ? "تم إلغاء العملية." : "Operation cancelled."; }
        catch (Exception ex) { _status.Text = (_isArabic ? "تعذر ربط Gmail: " : "Could not connect Gmail: ") + ex.Message; }
        finally { EndOperation(cancellation); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        if (!_service.IsConnected) { _status.Text = _isArabic ? "اربط Gmail أولًا." : "Connect Gmail first."; return; }
        var cancellation = BeginOperation();
        try { await LoadMessagesAsync(cancellation.Token); }
        finally { EndOperation(cancellation); }
    }

    private async Task LoadMessagesAsync(CancellationToken token)
    {
        try
        {
            _messageClear();
            var messages = await _service.ListMessagesAsync(_query.Text.Trim(), 12, token);
            foreach (var item in messages) _messages.Add(item);
            _status.Text = messages.Count == 0
                ? (_isArabic ? "لا توجد رسائل مطابقة." : "No matching messages.")
                : (_isArabic ? $"تم تحميل {messages.Count} رسالة. اختر واحدة للمعاينة." : $"Loaded {messages.Count} messages. Select one to preview.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _status.Text = (_isArabic ? "تعذر تحميل الرسائل: " : "Could not load messages: ") + ex.Message; }
    }

    private void _messageClear()
    {
        _messages.Clear();
        _list.SelectedItem = null;
        _selectedDetail = null;
        _subject.Text = string.Empty;
        _metadata.Text = string.Empty;
        _body.Clear();
        _addToDraft.IsEnabled = false;
    }

    private async void MessageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_list.SelectedItem is not GmailMessageSummary summary) return;
        _readOperation?.Cancel();
        _readOperation?.Dispose();
        _readOperation = new CancellationTokenSource();
        var token = _readOperation.Token;
        _selectedDetail = null;
        _addToDraft.IsEnabled = false;
        _subject.Text = summary.Subject;
        _metadata.Text = (_isArabic ? "من: " : "From: ") + summary.From + Environment.NewLine +
            (_isArabic ? "التاريخ: " : "Received: ") + (summary.ReceivedAt?.ToLocalTime().ToString("g") ?? "—");
        _body.Text = summary.Snippet;
        try
        {
            _status.Text = _isArabic ? "جارٍ جلب نص الرسالة..." : "Loading email text…";
            var detail = await _service.ReadMessageAsync(summary.Id, token);
            if (token.IsCancellationRequested || !ReferenceEquals(_list.SelectedItem, summary)) return;
            _selectedDetail = detail;
            _subject.Text = detail.Subject;
            _metadata.Text = (_isArabic ? "من: " : "From: ") + detail.From +
                Environment.NewLine + (_isArabic ? "إلى: " : "To: ") + detail.To +
                Environment.NewLine + (_isArabic ? "التاريخ: " : "Received: ") + (detail.ReceivedAt?.ToLocalTime().ToString("g") ?? "—");
            _body.Text = detail.PlainTextBody;
            _addToDraft.IsEnabled = true;
            _status.Text = _isArabic ? "المعاينة محلية. لا مشاركة مع AI حتى ترسل من الشات." : "Local preview. Nothing is shared with AI unless you send it from chat.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) _status.Text = (_isArabic ? "تعذرت قراءة الرسالة: " : "Could not read message: ") + ex.Message; }
    }

    private void AddToDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDetail is not { } detail) return;
        var text = (_isArabic ? "[بريد Gmail للمراجعة]" : "[Gmail email for review]") +
            Environment.NewLine + (_isArabic ? "من: " : "From: ") + detail.From +
            Environment.NewLine + (_isArabic ? "إلى: " : "To: ") + detail.To +
            Environment.NewLine + (_isArabic ? "العنوان: " : "Subject: ") + detail.Subject +
            Environment.NewLine + (_isArabic ? "التاريخ: " : "Received: ") + (detail.ReceivedAt?.ToLocalTime().ToString("g") ?? "—") +
            Environment.NewLine + Environment.NewLine + detail.PlainTextBody;
        var answer = MessageBox.Show(this,
            _isArabic
                ? "سيُنسخ البريد إلى مسودة الشات فقط. لن يُرسل حتى تضغط زر الإرسال بنفسك؛ راجع المحتوى قبل الإرسال. هل تريد الإضافة؟"
                : "The email will be copied into the chat draft only. It will not be sent until you press Send; review it before sending. Add it?",
            _isArabic ? "تأكيد إضافة البريد للمسودة" : "Confirm add email to draft",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _addToChatDraft(text);
        Close();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            _isArabic
                ? "سيُحذف الرمز المشفر من هذا الجهاز فقط. لإلغاء تفويض Google نهائيًا، أزل التطبيق من إعدادات أمان حساب Google. هل تريد الفصل؟"
                : "The encrypted token will be deleted from this device only. To revoke Google's grant, remove the app from Google Account security settings. Disconnect?",
            _isArabic ? "فصل Gmail" : "Disconnect Gmail",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _service.Disconnect();
        _messageClear();
        _status.Text = _isArabic ? "تم حذف الرمز المحلي." : "Local token deleted.";
        UpdateConnectionControls();
    }

    private void UpdateConnectionControls()
    {
        var busy = _operation is not null;
        var connected = _service.IsConnected;
        _connect.IsEnabled = !busy;
        _refresh.IsEnabled = !busy && connected;
        _disconnect.IsEnabled = !busy && connected;
        _search.IsEnabled = !busy && connected;
        _addToDraft.IsEnabled = !busy && _selectedDetail is not null;
        if (connected && !busy) _connect.Content = _isArabic ? "إعادة ربط" : "Reconnect";
        else if (!connected) _connect.Content = _isArabic ? "ربط Gmail" : "Connect Gmail";
    }

    protected override void OnClosed(EventArgs e)
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _readOperation?.Cancel();
        _readOperation?.Dispose();
        base.OnClosed(e);
    }
}
