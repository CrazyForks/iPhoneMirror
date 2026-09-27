using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

internal sealed class ReverseControlStatusViewModel : INotifyPropertyChanged
{
    private readonly ControlStatusService _service;
    private readonly DispatcherTimer _timer;
    private readonly System.Threading.Timer _deadlineTimer;
    private ControlStatusSnapshot? _snapshot;
    private int _remaining = 5;
    private DateTime _autoCloseAtUtc;
    private int _countdownElapsed;
    private int _countdownArmed;
    private bool _details;
    public ObservableCollection<ControlStageItem> Stages { get; } = [];
    public ObservableCollection<string> Diagnostics { get; } = [];
    public ObservableCollection<ControlPromptOption> PromptOptions { get; } = [];
    private ControlPromptOption? _selectedPromptOption;
    public string DeviceSummary => $"{_snapshot?.DeviceName ?? "iPhone"} · {_snapshot?.Mode switch { ControlStatusMode.Usb => "USB", ControlStatusMode.Wireless => "无线", ControlStatusMode.Bluetooth => "蓝牙", _ => "" }}";
    public string StageTitle => GetStageTitle(_snapshot?.Stage);
    public string StageDescription => _snapshot?.Error ?? _snapshot?.Description ?? "正在准备控制连接…";
    public string CountdownText => _snapshot?.Stage == ControlStage.Ready ? $"窗口将在 {_remaining} 秒后关闭" : string.Empty;
    public string TotalDurationText => _snapshot?.Duration is { } duration
        ? $"本次连接耗时 {duration.TotalSeconds:0.0} 秒" : string.Empty;
    public Visibility CancelVisibility => _snapshot is { CanCancel: true, IsTerminal: false } ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CloseVisibility => _snapshot is { IsTerminal: true } ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RetryVisibility => _snapshot is { Stage: ControlStage.Failed } && RetryRequested is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PromptVisibility => _snapshot?.Prompt is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility BluetoothPairingStepsVisibility =>
        _snapshot is
        {
            Mode: ControlStatusMode.Bluetooth,
            Stage: ControlStage.WaitingForPhoneConnection,
            Prompt: null,
        }
            ? Visibility.Visible
            : Visibility.Collapsed;
    public string BluetoothPairStepOneText => LocalizationService.Get("BluetoothControlPairStepOneFormat");
    public string BluetoothPairStepTwoText => LocalizationService.Format(
        "BluetoothControlPairStepTwo", Environment.MachineName);
    public string BluetoothPairStepThreeText => LocalizationService.Get("BluetoothControlPairStepThree");
    public string BluetoothPairStepFourText => LocalizationService.Get("BluetoothControlPairStepFour");
    public string BluetoothPairStepFiveText => LocalizationService.Get("BluetoothControlPairStepFive");
    public string PromptTitle => _snapshot?.Prompt?.Title ?? string.Empty;
    public string PromptMessage => _snapshot?.Prompt?.Message ?? string.Empty;
    public string PromptPrimaryText => _snapshot?.Prompt?.PrimaryButtonText ?? "继续";
    public string PromptSecondaryText => _snapshot?.Prompt?.SecondaryButtonText ?? "取消";
    public Visibility PromptPrimaryVisibility => _snapshot?.Prompt?.PrimaryButtonText is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PromptSecondaryVisibility => _snapshot?.Prompt?.SecondaryButtonText is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PromptOptionsVisibility => PromptOptions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string PromptTechnicalDetails => _snapshot?.Prompt?.TechnicalDetails ?? string.Empty;
    public Visibility PromptTechnicalDetailsVisibility => string.IsNullOrWhiteSpace(PromptTechnicalDetails) ? Visibility.Collapsed : Visibility.Visible;
    public bool IsPromptPrimaryEnabled => _snapshot?.Prompt?.Options is null || SelectedPromptOption?.IsEnabled == true;
    public Brush PromptAccentBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(
        _snapshot?.Prompt?.Type switch
        {
            ControlPromptType.Error => "#C54242",
            ControlPromptType.Warning => "#B7791F",
            ControlPromptType.UserActionRequired => "#2878B5",
            _ => "#3E7CB1",
        }));
    public ControlPromptOption? SelectedPromptOption
    {
        get => _selectedPromptOption;
        set { if (ReferenceEquals(_selectedPromptOption, value)) return; _selectedPromptOption = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsPromptPrimaryEnabled)); }
    }
    public bool IsDetailsExpanded { get => _details; set { _details = value; OnPropertyChanged(); } }
    internal Action? CancelRequested { get; set; }
    internal Action? RetryRequested { get; set; }
    internal event Action? CountdownElapsed;
    internal ReverseControlStatusViewModel(ControlStatusService service)
    {
        // Tick more frequently than the displayed whole seconds. The deadline
        // is absolute, so a delayed UI tick cannot freeze the label at 5 or
        // extend the window lifetime.
        _service = service;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
        _deadlineTimer = new System.Threading.Timer(_ => OnCountdownDeadline(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        service.StatusChanged += OnStatusChanged; Apply(service.Current);
    }
    private void OnStatusChanged(object? sender, ControlStatusSnapshot snapshot)
    {
        if (snapshot.Stage == ControlStage.Ready && snapshot.Prompt is null)
            ArmCountdown();
        else if (snapshot.Stage != ControlStage.Ready || snapshot.Prompt is not null)
        {
            Interlocked.Exchange(ref _countdownArmed, 0);
            _deadlineTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        Application.Current?.Dispatcher.BeginInvoke(() => Apply(snapshot));
    }

    private void ArmCountdown()
    {
        if (Interlocked.Exchange(ref _countdownArmed, 1) != 0) return;
        _autoCloseAtUtc = DateTime.UtcNow.AddSeconds(5);
        Interlocked.Exchange(ref _countdownElapsed, 0);
        _deadlineTimer.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }
    private void Apply(ControlStatusSnapshot? snapshot)
    {
        if (snapshot is null) return;
        var wasReady = _snapshot?.Stage == ControlStage.Ready;
        var hadPrompt = _snapshot?.Prompt is not null;
        _snapshot = snapshot;
        Stages.Clear();
        foreach (var stage in GetWorkflowStages(snapshot.Mode))
            Stages.Add(new(stage, _service));
        Diagnostics.Clear(); foreach (var item in _service.Diagnostics)
            Diagnostics.Add($"[{item.Timestamp:HH:mm:ss.fff}] [{item.Level}] {item.Message} | {item.TechnicalMessage}");
        PromptOptions.Clear();
        if (snapshot.Prompt?.Options is { } options)
            foreach (var option in options) PromptOptions.Add(option);
        SelectedPromptOption = PromptOptions.FirstOrDefault();
        if (snapshot.Prompt is not null)
        {
            _timer.Stop();
            _deadlineTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        else if (snapshot.Stage == ControlStage.Ready && (!wasReady || hadPrompt))
        {
            ArmCountdown();
            _remaining = Math.Max(1, (int)Math.Ceiling(
                (_autoCloseAtUtc - DateTime.UtcNow).TotalSeconds));
            _timer.Stop();
            _timer.Start();
        }
        else if (snapshot.Stage != ControlStage.Ready)
        {
            _timer.Stop();
            _deadlineTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        NotifyAll();
    }
    private static IReadOnlyList<ControlStage> GetWorkflowStages(ControlStatusMode mode) =>
        ControlStageWorkflow.GetStages(mode);
    internal static string GetStageTitle(ControlStage? stage) => stage switch
    {
        ControlStage.CheckingDevice => "检查设备",
        ControlStage.CheckingBinding => "检查设备绑定",
        ControlStage.CheckingBluetooth => "检查电脑蓝牙",
        ControlStage.SwitchingBluetoothPeripheral => "切换蓝牙外设模式",
        ControlStage.WaitingForPhoneConnection => "等待手机连接",
        ControlStage.VerifyingTouch => "检查触控功能",
        ControlStage.CheckingPermissions => "检查设备权限",
        ControlStage.PreparingDeviceSupport => "准备设备支持文件",
        ControlStage.Connecting => "建立控制连接",
        ControlStage.InitializingServices => "初始化控制服务",
        ControlStage.StartingInputRouter => "启动输入控制",
        ControlStage.Ready => "控制已连接",
        ControlStage.Recovering => "正在恢复控制连接",
        ControlStage.Failed => "无法启用反向控制",
        ControlStage.Cancelled => "已取消反向控制",
        _ => "反向控制",
    };
    private void OnTick(object? sender, EventArgs e)
    {
        if (_snapshot?.Stage != ControlStage.Ready)
        {
            _timer.Stop();
            return;
        }

        // DispatcherTimer ticks can be delayed by a busy UI thread. Calculate
        // from an absolute deadline so delayed ticks cannot leave "5 seconds"
        // visible indefinitely or extend the close window.
        _remaining = Math.Max(0, (int)Math.Ceiling((_autoCloseAtUtc - DateTime.UtcNow).TotalSeconds));
        if (_remaining <= 0)
        {
            _timer.Stop();
            return;
        }
        OnPropertyChanged(nameof(CountdownText));
    }
    private void OnCountdownDeadline()
    {
        // A callback queued from an older countdown must not finish a newer
        // one early. The absolute deadline is the authority.
        if (Volatile.Read(ref _countdownArmed) == 0 ||
            DateTime.UtcNow < _autoCloseAtUtc ||
            Interlocked.Exchange(ref _countdownElapsed, 1) != 0)
            return;
        CountdownElapsed?.Invoke();
    }

    internal void Dispose()
    {
        _timer.Stop();
        _deadlineTimer.Dispose();
        _service.StatusChanged -= OnStatusChanged;
    }
    internal bool HasPrompt => _snapshot?.Prompt is not null;
    internal void RefreshActions()
    {
        OnPropertyChanged(nameof(CancelVisibility));
        OnPropertyChanged(nameof(CloseVisibility));
        OnPropertyChanged(nameof(RetryVisibility));
    }
    internal void ResolvePrompt(ControlPromptAction action)
    {
        var value = action == ControlPromptAction.Primary ? SelectedPromptOption?.Id : null;
        _service.ResolvePrompt(new(action, value));
    }
    private void NotifyAll()
    {
        foreach (var p in new[] { nameof(DeviceSummary), nameof(StageTitle), nameof(StageDescription), nameof(CountdownText), nameof(TotalDurationText), nameof(CancelVisibility), nameof(CloseVisibility), nameof(RetryVisibility), nameof(PromptVisibility), nameof(BluetoothPairingStepsVisibility), nameof(PromptTitle), nameof(PromptMessage), nameof(PromptPrimaryText), nameof(PromptSecondaryText), nameof(PromptPrimaryVisibility), nameof(PromptSecondaryVisibility), nameof(PromptOptionsVisibility), nameof(PromptTechnicalDetails), nameof(PromptTechnicalDetailsVisibility), nameof(IsPromptPrimaryEnabled), nameof(PromptAccentBrush) }) OnPropertyChanged(p);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

internal sealed class ControlStageItem(ControlStage stage, ControlStatusService service)
{
    public string Title => ReverseControlStatusViewModel.GetStageTitle(stage);
    public string Marker => service.GetProgress(stage) switch
    {
        ControlStageProgress.Completed => "✓",
        ControlStageProgress.Failed => "×",
        ControlStageProgress.Active => "●",
        ControlStageProgress.Skipped => "–",
        _ => "○",
    };
    public Brush MarkerBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(service.GetProgress(stage) switch
    {
        ControlStageProgress.Completed => "#68C58A",
        ControlStageProgress.Failed => "#FF6B6B",
        ControlStageProgress.Active => "#8FC8FF",
        _ => "#A6A6A6",
    }));
    public string DurationText => service.GetProgress(stage) switch
    {
        ControlStageProgress.Active => "…",
        ControlStageProgress.Completed or ControlStageProgress.Failed
            when service.GetDuration(stage) is { } value => $"{value.TotalSeconds:0.0}s",
        _ => string.Empty,
    };
}

public partial class ReverseControlStatusWindow : Wpf.Ui.Controls.FluentWindow
{
    private static ReverseControlStatusWindow? _active;
    private readonly ReverseControlStatusViewModel _viewModel;
    private Action? _countdownElapsed;
    private nint _nativeHandle;
    private int _hiddenAtDeadline;

    private ReverseControlStatusWindow(Window owner, ControlStatusService service, Action? cancel,
        Action? retry, Action? countdownElapsed)
    {
        Owner = owner;
        _countdownElapsed = countdownElapsed;
        _viewModel = new(service) { CancelRequested = cancel, RetryRequested = retry };
        _viewModel.CountdownElapsed += OnCountdownElapsed;
        DataContext = _viewModel;
        InitializeComponent();
        SourceInitialized += (_, _) =>
            _nativeHandle = new WindowInteropHelper(this).Handle;
        Closed += (_, _) => { _viewModel.Dispose(); if (ReferenceEquals(_active, this)) _active = null; };
    }
    internal static void Show(Window owner, ControlStatusService service, Action? cancel = null,
        Action? retry = null, Action? countdownElapsed = null)
    {
        if (_active is { IsVisible: true } active)
        {
            if (cancel is not null) active._viewModel.CancelRequested = cancel;
            if (retry is not null) active._viewModel.RetryRequested = retry;
            if (countdownElapsed is not null) active._countdownElapsed = countdownElapsed;
            active._viewModel.RefreshActions();
            active.Activate();
            return;
        }
        _active = new(owner, service, cancel, retry, countdownElapsed);
        _active.Show();
        _active.Activate();
    }
    internal static void ShowDeveloperPreview(Window owner)
    {
        var service = new ControlStatusService();
        service.Report(ControlStatusMode.Usb, ControlStage.Connecting, "iPhone",
            "正在建立控制连接…", canCancel: false);
        new ReverseControlStatusWindow(owner, service, null, null, null).Show();
    }
    internal static void CloseActive()
    {
        if (_active is { } window)
        {
            if (!window.Dispatcher.CheckAccess())
            {
                _ = window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Send, new Action(CloseActive));
                return;
            }
            _active = null;
            window.Close();
        }
    }
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.HasPrompt) _viewModel.ResolvePrompt(ControlPromptAction.Cancel);
        Close();
    }
    private void OnCancelClick(object sender, RoutedEventArgs e) { _viewModel.ResolvePrompt(ControlPromptAction.Cancel); _viewModel.CancelRequested?.Invoke(); Close(); }
    private void OnPromptPrimaryClick(object sender, RoutedEventArgs e) => _viewModel.ResolvePrompt(ControlPromptAction.Primary);
    private void OnPromptSecondaryClick(object sender, RoutedEventArgs e) => _viewModel.ResolvePrompt(ControlPromptAction.Secondary);
    private void OnRetryClick(object sender, RoutedEventArgs e) => _viewModel.RetryRequested?.Invoke();

    private void OnCountdownElapsed()
    {
        // ShowWindow is safe from the timer callback and hides the native
        // surface immediately even while the WPF dispatcher is busy. Close is
        // still queued to release managed resources once that dispatcher runs.
        if (Interlocked.Exchange(ref _hiddenAtDeadline, 1) != 0) return;
        var handle = _nativeHandle;
        if (handle != 0) _ = ShowWindow(handle, SwHide);
        if (!Dispatcher.HasShutdownStarted)
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                Close();
                // Enable input only after the status surface has been removed
                // from the active window stack. This prevents the fallback
                // countdown from exposing a live controller behind a dialog
                // that still displays one second remaining.
                _countdownElapsed?.Invoke();
            }));
    }

    private const int SwHide = 0;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
}
