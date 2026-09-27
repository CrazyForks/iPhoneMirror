using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace IPhoneMirror.App.Services;

internal enum BluetoothHidStartupStage
{
    CheckingBluetooth,
    SwitchingToPeripheral,
}

/// <summary>
/// Exposes the Windows Bluetooth radio as a standard BLE HID mouse/keyboard.
/// iOS consumes this as a normal pointer device when AssistiveTouch is enabled.
/// </summary>
internal sealed class BluetoothHidMouseService : IAsyncDisposable
{
    internal const int ReportMapVersion = BluetoothHidProtocol.ReportMapVersion;
    private static readonly Guid HidServiceUuid = GattServiceUuids.HumanInterfaceDevice;
    private static readonly Guid ReportUuid = GattCharacteristicUuids.Report;
    private static readonly Guid BootKeyboardInputUuid =
        Guid.Parse("00002a22-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BootMouseInputUuid =
        Guid.Parse("00002a33-0000-1000-8000-00805f9b34fb");
    private static readonly Guid ReportReferenceUuid = Guid.Parse("00002908-0000-1000-8000-00805f9b34fb");
    private static readonly byte[] HidInformation = [0x11, 0x01, 0x00, 0x03];
    private static readonly byte[] DefaultProtocolMode = [0x01];
    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromSeconds(2);
    // Pointer reports are disposable snapshots. A notification that has not
    // completed quickly enough is already historical and must be dropped.
    private static readonly TimeSpan MouseNotificationTimeout =
        TimeSpan.FromMilliseconds(300);
    // iOS/Windows BLE HID links commonly service input notifications around
    // 50-60 Hz. Sending faster than the link can drain creates a controller
    // queue that continues moving the phone after the mouse has stopped.
    // Keep input sampling at 250 Hz, but pace BLE notifications near the
    // connection interval. A 4 ms producer cadence can outrun iOS/Windows
    // GATT delivery and create a repeating transport stall.
    private static readonly TimeSpan MouseReportInterval = TimeSpan.FromMilliseconds(8);

    // Report 1 is keyboard and report 2 is mouse. Keeping both reports in one
    // HID service lets iOS expose pointer and keyboard input from one pairing.
    private static readonly byte[] ReportMap =
    [
        0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x85, 0x01,
        0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01,
        0x75, 0x01, 0x95, 0x08, 0x81, 0x02, 0x75, 0x08, 0x95, 0x01,
        0x81, 0x01, 0x75, 0x08, 0x95, 0x06, 0x15, 0x00, 0x25, 0x65,
        0x05, 0x07, 0x19, 0x00, 0x29, 0x65, 0x81, 0x00, 0xC0,
        0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x85, 0x02, 0x09, 0x01,
        0xA1, 0x00, 0x05, 0x09, 0x19, 0x01, 0x29, 0x05, 0x15, 0x00,
        0x25, 0x01, 0x75, 0x01, 0x95, 0x05, 0x81, 0x02, 0x75, 0x03,
        0x95, 0x01, 0x81, 0x01, 0x05, 0x01, 0x09, 0x30, 0x09, 0x31,
        0x16, 0x01, 0x80, 0x26, 0xFF, 0x7F, 0x75, 0x10, 0x95, 0x02,
        0x81, 0x06, 0x09, 0x38, 0x15, 0x81, 0x25, 0x7F, 0x75, 0x08,
        0x95, 0x01, 0x81, 0x06,
        0x85, 0x03,
        0x09, 0x48, 0x15, 0x00, 0x25, 0x0A, 0x75, 0x08, 0x95, 0x01,
        0xB1, 0x02, 0xC0, 0xC0,
        0x05, 0x0C, 0x09, 0x01, 0xA1, 0x01, 0x85, 0x04,
        0x15, 0x00, 0x26, 0xFF, 0x03, 0x75, 0x10, 0x95, 0x01,
        0x0A, 0x9D, 0x02, 0x81, 0x02, 0xC0,
        // Navigation controls are separate from the Globe/Fn modifier report.
        // This mirrors standard external-keyboard Consumer Control usages used
        // by iPadOS for Back and Menu/recent-tasks.
        0x05, 0x0C, 0x09, 0x01, 0xA1, 0x01, 0x85, 0x05,
        0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x0D,
        0x0A, 0x24, 0x02, 0x09, 0x40, 0x0A, 0x23, 0x02,
        0x0A, 0xAE, 0x01, 0x0A, 0x21, 0x02, 0x81, 0x02,
        0x95, 0x03, 0x75, 0x01, 0x81, 0x03, 0xC0
    ];

    private const ushort GlobeKeyboardLayoutUsage = 0x029D;
    private const ushort NavigationMenu = 0x0002;
    private static readonly TimeSpan AppSwitcherDoublePressInterval =
        TimeSpan.FromMilliseconds(100);

    private readonly SemaphoreSlim _gate = new(1, 1);
    // NotifyValueAsync can outlive the Task returned by AsTask(token) when
    // WinRT cancels its managed wrapper. Keep one transport slot for mouse
    // notifications so a timed-out native operation can never overlap a
    // newer report and create delayed cursor movement.
    private readonly SemaphoreSlim _mouseNotificationTransportGate = new(1, 1);
    private readonly object _notificationChannelSync = new();
    private NotificationChannel _notificationChannel = new();
    private NotificationChannel _mouseNotificationChannel = new();
    private readonly SemaphoreSlim _targetClientGate = new(1, 1);
    private readonly SemaphoreSlim _clientRefreshGate = new(1, 1);
    private readonly object _clientRefreshTrackingSync = new();
    private readonly object _mousePumpSync = new();
    private readonly BluetoothClientRouteTable _clientRoutes = new();
    private readonly ConcurrentDictionary<byte, byte[]> _lastReports = new();
    private readonly ConcurrentDictionary<string, ClientState> _clientStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GattSession> _clientSessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _clientConnectedAt =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Task, byte> _clientRefreshTasks = new();
    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _mouseReport;
    private GattLocalCharacteristic? _keyboardReport;
    private GattLocalCharacteristic? _consumerReport;
    private GattLocalCharacteristic? _navigationReport;
    private GattLocalCharacteristic? _bootMouseInput;
    private GattLocalCharacteristic? _bootKeyboardInput;
    private GattLocalCharacteristic? _protocolModeCharacteristic;
    private GattLocalCharacteristic? _wheelResolutionCharacteristic;
    private TaskCompletionSource<bool>? _advertisingStarted;
    private TaskCompletionSource<bool>? _advertisingStopped;
    private int _advertisingStartupInProgress;
    private TaskCompletionSource<bool>? _clientConnected;
    private Task? _mousePumpTask;
    private byte[]? _pendingMouseReport;
    private long _pendingMouseReportTimestamp;
    private readonly Queue<byte[]> _mousePriorityReports = new();
    private readonly Queue<(byte ReportId, byte[] Report, TaskCompletionSource<bool> Completion)>
        _keyboardPriorityReports = new();
    private bool _mousePumpRunning;
    private bool _mousePumpStopping;
    private byte _lastQueuedMouseButtons;
    private int _transportFailed;
    private int _advertisingStopRequested;
    private int _disposed;
    private string? _targetClientId;
    private string? _targetDeviceUdid;
    private string? _targetDeviceName;
    private string? _preferredClientId;
    private int _routeGeneration;

    public bool IsAdvertising => _provider?.AdvertisementStatus is
        GattServiceProviderAdvertisementStatus.Started or
        GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData;
    public bool IsConnected
    {
        get
        {
            if (Volatile.Read(ref _transportFailed) != 0) return false;
            try { return IsMouseConnected; }
            catch (COMException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }
    }
    internal bool HasTransportFailure => Volatile.Read(ref _transportFailed) != 0;
    internal bool IsMouseReady => IsConnected;
    public int WheelResolutionMultiplier => GetTargetClientState()?.WheelResolutionMultiplier ?? 1;
    private bool IsMouseConnected => HasTargetSubscriber(_mouseReport) ||
        HasTargetSubscriber(_bootMouseInput);
    private bool HasAnySubscriber => HasSubscribers(_mouseReport) ||
        HasSubscribers(_bootMouseInput) || HasSubscribers(_keyboardReport) ||
        HasSubscribers(_consumerReport) || HasSubscribers(_navigationReport) ||
        HasSubscribers(_bootKeyboardInput);
    public string SuggestedDeviceName { get; } = Environment.MachineName;
    public string Status { get; private set; } = "Bluetooth control is off";
    public string? Error { get; private set; }

    public event EventHandler? StatusChanged;
    internal string? TargetClientId => Volatile.Read(ref _targetClientId);
    internal bool IsTargetClientConnected => IsMouseConnected;

    private string ClientConnectionStatus => IsConnected
        ? "A selected Bluetooth HID client is connected."
        : HasAnySubscriber
            ? "Bluetooth clients are connected; waiting for the selected iPhone/iPad."
            : "Bluetooth HID control is advertising; waiting for a client.";

    internal async Task<bool> WaitForConnectionAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (IsMouseConnected) return true;
        var waiter = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _clientConnected = waiter;
        if (IsMouseConnected) waiter.TrySetResult(true);
        try
        {
            var completed = await Task.WhenAny(waiter.Task,
                Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
            return completed == waiter.Task && await waiter.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(_clientConnected, waiter)) _clientConnected = null;
        }
    }

    public async Task<bool> StartAsync(string targetDeviceUdid,
        string? targetDeviceName = null,
        bool preserveExistingBinding = false,
        string? preferredClientId = null,
        CancellationToken cancellationToken = default,
        Action<BluetoothHidStartupStage>? startupProgress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceUdid);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await BeginTargetRouteAsync(targetDeviceUdid,
                targetDeviceName,
                clearPreviousBinding: !IsAdvertising && !preserveExistingBinding,
                preferredClientId)
                .ConfigureAwait(false);
            startupProgress?.Invoke(BluetoothHidStartupStage.CheckingBluetooth);
            if (IsAdvertising && Volatile.Read(ref _advertisingStopRequested) == 0)
            {
                await RefreshTargetClientAsync().ConfigureAwait(false);
                return true;
            }
            if (IsAdvertising)
            {
                StopAndClearProviderState();
                await BeginTargetRouteAsync(targetDeviceUdid, targetDeviceName,
                    preferredClientId: preferredClientId)
                    .ConfigureAwait(false);
            }
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null || !adapter.IsLowEnergySupported)
            {
                SetStatus("This Bluetooth adapter cannot act as a BLE peripheral.",
                    "The Bluetooth adapter does not support Bluetooth Low Energy.");
                return false;
            }
            // Some Windows Bluetooth drivers report the peripheral capability
            // bit as false even though GATT service providers can advertise.
            // Let Create/StartAdvertising be the authoritative test, as this
            // is the path that works on the MediaTek adapter used by v1.8.1.
            SetStatus(adapter.IsPeripheralRoleSupported
                ? "Bluetooth adapter supports BLE peripheral mode."
                : "Bluetooth adapter peripheral capability is unconfirmed; trying GATT advertising.", null);
            startupProgress?.Invoke(BluetoothHidStartupStage.SwitchingToPeripheral);

            if (_provider is null)
            {
                var result = await GattServiceProvider.CreateAsync(HidServiceUuid);
                if (result.Error != BluetoothError.Success || result.ServiceProvider is null)
                {
                    SetStatus("Could not create the Bluetooth HID service.",
                        $"GattServiceProvider.CreateAsync returned {result.Error}.");
                    return false;
                }

                _provider = result.ServiceProvider;
                _provider.AdvertisementStatusChanged += OnAdvertisementStatusChanged;
                await CreateCharacteristicsAsync(_provider.Service);
            }

            var advertising = new GattServiceProviderAdvertisingParameters
            {
                IsConnectable = true,
                IsDiscoverable = true,
            };
            Interlocked.Exchange(ref _advertisingStopRequested, 0);
            Interlocked.Exchange(ref _transportFailed, 0);
            _lastReports.Clear();
            TrackSubscribedClients();
            _advertisingStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _advertisingStartupInProgress, 1);
            _provider.StartAdvertising(advertising);
            var completed = await Task.WhenAny(_advertisingStarted.Task,
                Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            Interlocked.Exchange(ref _advertisingStartupInProgress, 0);
            if (completed != _advertisingStarted.Task || !IsAdvertising)
            {
                StopAndClearProviderState();
                SetStatus("Bluetooth HID control did not start advertising.",
                    "The Bluetooth stack did not report an advertising state.");
                return false;
            }
            SetStatus($"Bluetooth HID control is advertising ({_provider.AdvertisementStatus}).",
                null);
            await RefreshTargetClientAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref _advertisingStartupInProgress, 0);
            StopAndClearProviderState();
            SetStatus("Bluetooth HID control could not start.", error.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        RetireNotificationChannel();
        Task? mousePump;
        lock (_mousePumpSync)
        {
            _mousePumpStopping = true;
            _pendingMouseReport = null;
            _pendingMouseReportTimestamp = 0;
            _mousePriorityReports.Clear();
            while (_keyboardPriorityReports.Count > 0)
                _keyboardPriorityReports.Dequeue().Completion.TrySetCanceled();
            mousePump = _mousePumpTask;
        }
        if (mousePump is not null)
            await mousePump.ConfigureAwait(false);
        lock (_mousePumpSync)
        {
            _mousePumpStopping = false;
            _mousePumpTask = null;
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!await StopAdvertisingSessionAsync().ConfigureAwait(false))
                StopAndClearProviderState();
            SetStatus("Bluetooth control is off", null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SendMouseAsync(int dx, int dy, byte buttons = 0, int wheel = 0)
    {
        var x = (short)Math.Clamp(dx, short.MinValue + 1, short.MaxValue);
        var y = (short)Math.Clamp(dy, short.MinValue + 1, short.MaxValue);
        var encodedWheel = Math.Clamp(wheel, -127, 127);
        if (!IsAdvertising) return Task.CompletedTask;
        byte[] report =
        [buttons, (byte)(x & 0xFF), (byte)((x >> 8) & 0xFF),
            (byte)(y & 0xFF), (byte)((y >> 8) & 0xFF),
            unchecked((byte)(sbyte)encodedWheel)];
        lock (_mousePumpSync)
        {
            if (buttons != _lastQueuedMouseButtons)
            {
                QueuePendingMotionBeforePriorityReport();
                _lastQueuedMouseButtons = buttons;
                EnqueueLatestMousePriorityReport(report);
            }
            else if (wheel != 0)
            {
                QueuePendingMotionBeforePriorityReport();
                EnqueueLatestMousePriorityReport(report);
            }
            else
            {
                // NotifyValueAsync can take longer than a BLE connection
                // interval. Keep exactly one latest sample so a blocked
                // native notification cannot become a replay queue.
                // MergeRecentMotion(_pendingMouseReport, report) would replay
                // every relative count collected while BLE is busy. Keep one
                // disposable latest sample instead; old travel is latency.
                _pendingMouseReport = BluetoothMouseReportCoalescer
                    .MergePendingMotion(_pendingMouseReport, report);
                if (_pendingMouseReportTimestamp == 0)
                    _pendingMouseReportTimestamp = Stopwatch.GetTimestamp();
            }
            _lastReports[2] = report;
            if (_mousePumpRunning) return Task.CompletedTask;
            _mousePumpRunning = true;
            _mousePumpTask = PumpReportsAsync();
        }
        return Task.CompletedTask;
    }

    internal Task SendMouseAsync(int dx, int dy, byte buttons = 0, int wheel = 0,
        string? expectedTargetDeviceUdid = null)
    {
        if (expectedTargetDeviceUdid is not null &&
            !string.Equals(_targetDeviceUdid, expectedTargetDeviceUdid,
                StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        return SendMouseAsync(dx, dy, buttons, wheel);
    }

    private void QueuePendingMotionBeforePriorityReport()
    {
        if (_pendingMouseReport is null) return;

        // Button and wheel transitions are latency-sensitive. Never move old
        // relative travel into the priority queue: that would turn one BLE
        // stall into a replay burst immediately before the click/scroll.
        _pendingMouseReport = null;
        _pendingMouseReportTimestamp = 0;
    }

    private void EnqueueLatestMousePriorityReport(byte[] report)
    {
        // A button/wheel report is also relative state. Keep one pending
        // transition behind the in-flight BLE notification; retaining a FIFO
        // here would replay stale clicks or scroll ticks after a stall.
        _mousePriorityReports.Clear();
        _mousePriorityReports.Enqueue(report);
    }

    private async Task PumpReportsAsync()
    {
        while (true)
        {
            byte[]? report = null;
            byte reportId = 2;
            TaskCompletionSource<bool>? completion = null;
            long queuedAt = 0;
            lock (_mousePumpSync)
            {
                if (_mousePumpStopping)
                {
                    _mousePumpRunning = false;
                    return;
                }
                // Keyboard state changes are latency-sensitive. Always drain
                // them before coalesced mouse motion so pointer traffic cannot
                // starve key presses or releases.
                if (_keyboardPriorityReports.Count > 0)
                {
                    var item = _keyboardPriorityReports.Dequeue();
                    report = item.Report;
                    completion = item.Completion;
                    reportId = item.ReportId;
                }
                else if (_mousePriorityReports.Count > 0)
                {
                    report = _mousePriorityReports.Dequeue();
                }
                else if (_pendingMouseReport is not null)
                {
                    report = _pendingMouseReport;
                    _pendingMouseReport = null;
                    queuedAt = _pendingMouseReportTimestamp;
                    _pendingMouseReportTimestamp = 0;
                    if (queuedAt != 0 &&
                        (Stopwatch.GetTimestamp() - queuedAt) * 1000.0 /
                            Stopwatch.Frequency > MouseNotificationTimeout.TotalMilliseconds)
                        report = null;
                }
                else
                {
                    _mousePumpRunning = false;
                    return;
                }
            }
            if (report is null)
                continue;
            try
            {
                var sent = await SendReportAsync(reportId, report).ConfigureAwait(false);
                if (sent) completion?.TrySetResult(true);
                else
                {
                    completion?.TrySetException(new IOException(
                        "The selected Bluetooth HID client is not available."));
                    if (reportId == 2)
                    {
                        // A timed-out notification may still be held by the
                        // Windows stack. Drop movement and wheel history, but
                        // retain the newest button state so a release cannot
                        // be lost while the mouse is stationary.
                        lock (_mousePumpSync)
                        {
                            _pendingMouseReport = null;
                            _pendingMouseReportTimestamp = 0;
                            var buttonState = report.Length > 0 ? report[0] :
                                _lastQueuedMouseButtons;
                            while (_mousePriorityReports.Count > 0)
                            {
                                var pending = _mousePriorityReports.Dequeue();
                                if (pending.Length > 0 && pending[5] == 0)
                                    buttonState = pending[0];
                            }
                            _mousePriorityReports.Clear();
                            _mousePriorityReports.Enqueue(
                                [buttonState, 0, 0, 0, 0, 0]);
                            _lastQueuedMouseButtons = buttonState;
                            _mousePumpRunning = false;
                        }
                        if (!TryAcquireMouseTransportGate())
                            _ = ResumeMousePumpAfterTransportAsync();
                        else
                        {
                            // There was no native operation left to wait for;
                            // avoid spinning forever when the client has gone
                            // away or the route is no longer valid.
                            lock (_mousePumpSync)
                                _mousePriorityReports.Clear();
                        }
                        return;
                    }
                }

                // NotifyValueAsync can complete after Windows queues the
                // packet, rather than after the next BLE connection event.
                // Pacing mouse reports here prevents an invisible queue from
                // becoming visible pointer lag after physical movement stops.
                if (reportId == 2)
                    await Task.Delay(MouseReportInterval).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                completion?.TrySetException(error);
            }
        }
    }

    private bool TryAcquireMouseTransportGate()
    {
        if (!_mouseNotificationTransportGate.Wait(0)) return false;
        _mouseNotificationTransportGate.Release();
        return true;
    }

    private async Task ResumeMousePumpAfterTransportAsync()
    {
        try
        {
            await _mouseNotificationTransportGate.WaitAsync().ConfigureAwait(false);
            _mouseNotificationTransportGate.Release();
        }
        catch (ObjectDisposedException) { return; }
        StartMousePumpIfNeeded();
    }

    private void StartMousePumpIfNeeded()
    {
        lock (_mousePumpSync)
        {
            if (_mousePumpStopping || _mousePumpRunning ||
                (_pendingMouseReport is null && _mousePriorityReports.Count == 0))
                return;
            _mousePumpRunning = true;
            _mousePumpTask = PumpReportsAsync();
        }
    }

    internal async Task ReleaseAllAsync()
    {
        Task? mousePump;
        lock (_mousePumpSync)
        {
            _pendingMouseReport = null;
            _pendingMouseReportTimestamp = 0;
            _mousePriorityReports.Clear();
            _lastQueuedMouseButtons = 0;
            while (_keyboardPriorityReports.Count > 0)
                _keyboardPriorityReports.Dequeue().Completion.TrySetCanceled();
            mousePump = _mousePumpTask;
        }
        if (mousePump is not null)
            await mousePump.ConfigureAwait(false);
        _ = await SendReportAsync(2, new byte[6]).ConfigureAwait(false);
        _ = await SendReportAsync(1, new byte[8]).ConfigureAwait(false);
        _ = await SendReportAsync(4, [0, 0]).ConfigureAwait(false);
        _ = await SendReportAsync(5, [0, 0]).ConfigureAwait(false);
    }

    internal Task<bool> CalibrateAsync(string targetDeviceUdid,
        CancellationToken cancellationToken = default)
    {
        var generation = Volatile.Read(ref _routeGeneration);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Relative HID reports have no absolute position. Do not send a
            // synthetic "center" report: 0x8001 is -32767 in the HID map and
            // would physically move the iPhone cursor during startup.
            return Task.FromResult(IsCurrentRoute(targetDeviceUdid, generation) &&
                IsMouseReady);
        }
        catch (OperationCanceledException) { return Task.FromResult(false); }
    }

    internal async Task<IReadOnlyList<BluetoothClientInfo>>
        GetSubscribedClientInfosAsync()
    {
        string[] clientIds;
        await _targetClientGate.WaitAsync().ConfigureAwait(false);
        try
        {
            clientIds = EnumerateSubscribedClients()
                .Select(client => client.Session.DeviceId.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally { _targetClientGate.Release(); }

        var clients = new List<BluetoothClientInfo>(clientIds.Length);
        foreach (var id in clientIds)
        {
            var name = await GetClientNameAsync(id).ConfigureAwait(false);
            var address = string.Empty;
            try
            {
                using var device = await BluetoothLEDevice.FromIdAsync(id);
                if (device is not null)
                    address = device.BluetoothAddress.ToString("X12");
            }
            catch { }
            clients.Add(new BluetoothClientInfo(id, name, address,
                _clientConnectedAt.TryGetValue(id, out var connectedAt)
                    ? connectedAt : DateTimeOffset.Now));
        }
        return clients;
    }

    internal async Task<bool> BindTargetClientAsync(string clientId)
    {
        await _targetClientGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsAdvertising || string.IsNullOrWhiteSpace(_targetDeviceUdid))
                return false;
            var clientIds = EnumerateSubscribedClients()
                .Select(client => client.Session.DeviceId.Id).ToArray();
            if (!_clientRoutes.SetBinding(_targetDeviceUdid, clientId, clientIds))
                return false;
            Volatile.Write(ref _targetClientId, clientId);
            AdvanceRouteGeneration();
            return true;
        }
        finally { _targetClientGate.Release(); }
    }

    internal Task<bool> BindTargetClientAsync(string targetDeviceUdid, string clientId)
    {
        if (!string.Equals(_targetDeviceUdid, targetDeviceUdid,
                StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);
        return BindTargetClientAsync(clientId);
    }

    public Task SendKeyboardAsync(byte modifiers, IReadOnlyCollection<byte> usages)
    {
        if (!IsAdvertising) return Task.CompletedTask;
        var report = new byte[8];
        report[0] = modifiers;
        var index = 2;
        foreach (var usage in usages.Take(6)) report[index++] = usage;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_mousePumpSync)
        {
            if (_mousePumpStopping) return Task.CompletedTask;
            // A keyboard report is a complete state snapshot. Supersede any
            // older unsent snapshot so rapid key transitions cannot build a
            // FIFO backlog behind a slow BLE notification.
            if (_keyboardPriorityReports.Count > 0)
            {
                var retained = new Queue<(byte ReportId, byte[] Report,
                    TaskCompletionSource<bool> Completion)>(_keyboardPriorityReports.Count);
                while (_keyboardPriorityReports.Count > 0)
                {
                    var pending = _keyboardPriorityReports.Dequeue();
                    if (pending.ReportId == 1)
                        pending.Completion.TrySetResult(false);
                    else
                        retained.Enqueue(pending);
                }
                while (retained.Count > 0)
                    _keyboardPriorityReports.Enqueue(retained.Dequeue());
            }
            _keyboardPriorityReports.Enqueue((1, report, completion));
            if (!_mousePumpRunning)
            {
                _mousePumpRunning = true;
                _mousePumpTask = PumpReportsAsync();
            }
        }
        return completion.Task;
    }

    internal Task SendKeyboardAsync(byte modifiers, IReadOnlyCollection<byte> usages,
        string? expectedTargetDeviceUdid)
    {
        if (expectedTargetDeviceUdid is not null &&
            !string.Equals(_targetDeviceUdid, expectedTargetDeviceUdid,
                StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        return SendKeyboardAsync(modifiers, usages);
    }

    private Task SendConsumerAsync(ushort usage)
    {
        if (!IsAdvertising) return Task.CompletedTask;
        var report = new[] { (byte)(usage & 0xFF), (byte)(usage >> 8) };
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_mousePumpSync)
        {
            if (_mousePumpStopping) return Task.CompletedTask;
            EnqueueLatestKeyboardPriorityReport(4, report, completion);
            if (!_mousePumpRunning)
            {
                _mousePumpRunning = true;
                _mousePumpTask = PumpReportsAsync();
            }
        }
        return completion.Task;
    }

    private Task SendNavigationAsync(ushort controls)
    {
        if (!IsAdvertising) return Task.CompletedTask;
        var report = new[] { (byte)(controls & 0xFF), (byte)(controls >> 8) };
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_mousePumpSync)
        {
            if (_mousePumpStopping) return Task.CompletedTask;
            EnqueueLatestKeyboardPriorityReport(5, report, completion);
            if (!_mousePumpRunning)
            {
                _mousePumpRunning = true;
                _mousePumpTask = PumpReportsAsync();
            }
        }
        return completion.Task;
    }

    private void EnqueueLatestKeyboardPriorityReport(byte reportId, byte[] report,
        TaskCompletionSource<bool> completion)
    {
        // Keyboard/consumer/navigation reports are complete state snapshots.
        // Supersede an older unsent snapshot of the same report ID, while
        // retaining other report IDs needed by a compound shortcut.
        if (_keyboardPriorityReports.Count > 0)
        {
            var retained = new Queue<(byte ReportId, byte[] Report,
                TaskCompletionSource<bool> Completion)>(_keyboardPriorityReports.Count);
            while (_keyboardPriorityReports.Count > 0)
            {
                var pending = _keyboardPriorityReports.Dequeue();
                if (pending.ReportId == reportId)
                    pending.Completion.TrySetResult(false);
                else
                    retained.Enqueue(pending);
            }
            while (retained.Count > 0)
                _keyboardPriorityReports.Enqueue(retained.Dequeue());
        }
        _keyboardPriorityReports.Enqueue((reportId, report, completion));
    }

    internal async Task SendIphoneSystemShortcutAsync(byte keyboardUsage)
    {
        Exception? failure = null;
        try
        {
            // Queue both pressed reports before awaiting either notification.
            // iPadOS recognizes Globe/Fn shortcuts only while the keyboard-layout
            // consumer control and keyboard usage overlap.
            var modifierPressed = SendConsumerAsync(GlobeKeyboardLayoutUsage);
            var keyPressed = SendKeyboardAsync(0, [keyboardUsage]);
            await Task.WhenAll(modifierPressed, keyPressed).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }

        try
        {
            var keyReleased = SendKeyboardAsync(0, []);
            var modifierReleased = SendConsumerAsync(0);
            await Task.WhenAll(keyReleased, modifierReleased).ConfigureAwait(false);
        }
        catch (Exception error) { failure ??= error; }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal Task SendIphoneSystemShortcutAsync(byte keyboardUsage,
        string? expectedTargetDeviceUdid)
    {
        if (expectedTargetDeviceUdid is not null &&
            !string.Equals(_targetDeviceUdid, expectedTargetDeviceUdid,
                StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        return SendIphoneSystemShortcutAsync(keyboardUsage);
    }

    internal async Task SendIphoneAppSwitcherAsync()
    {
        await SendNavigationControlAsync(NavigationMenu).ConfigureAwait(false);
        await Task.Delay(AppSwitcherDoublePressInterval).ConfigureAwait(false);
        await SendNavigationControlAsync(NavigationMenu).ConfigureAwait(false);
    }

    internal Task SendIphoneAppSwitcherAsync(string? expectedTargetDeviceUdid)
    {
        if (expectedTargetDeviceUdid is not null &&
            !string.Equals(_targetDeviceUdid, expectedTargetDeviceUdid,
                StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        return SendIphoneAppSwitcherAsync();
    }

    private async Task SendNavigationControlAsync(ushort controls)
    {
        Exception? failure = null;
        try { await SendNavigationAsync(controls).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        try { await SendNavigationAsync(0).ConfigureAwait(false); }
        catch (Exception error) { failure ??= error; }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task CreateCharacteristicsAsync(GattLocalService service)
    {
        _protocolModeCharacteristic = await CreateCharacteristicAsync(service,
            GattCharacteristicUuids.ProtocolMode,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Write,
            DefaultProtocolMode);
        _protocolModeCharacteristic.WriteRequested += OnProtocolModeWriteRequested;
        _protocolModeCharacteristic.ReadRequested += (sender, args) =>
            RespondToReadAsync(args, () => GetProtocolModeValue(args));
        _wheelResolutionCharacteristic = await CreateFeatureCharacteristicAsync(service);

        var hidInfo = await CreateCharacteristicAsync(service,
            GattCharacteristicUuids.HidInformation,
            GattCharacteristicProperties.Read, HidInformation);
        hidInfo.ReadRequested += (_, args) => RespondToReadAsync(args, () => HidInformation);
        _ = hidInfo;
        var reportMap = await CreateCharacteristicAsync(service,
            GattCharacteristicUuids.ReportMap,
            GattCharacteristicProperties.Read, ReportMap);
        reportMap.ReadRequested += (_, args) => RespondToReadAsync(args, () => ReportMap);
        _ = reportMap;
        var controlPoint = await CreateCharacteristicAsync(service,
            GattCharacteristicUuids.HidControlPoint,
            GattCharacteristicProperties.WriteWithoutResponse, [0x00]);
        controlPoint.WriteRequested += OnControlPointWriteRequested;

        _keyboardReport = await CreateReportCharacteristicAsync(service, 0x01);
        _mouseReport = await CreateReportCharacteristicAsync(service, 0x02);
        _consumerReport = await CreateReportCharacteristicAsync(service, 0x04);
        _navigationReport = await CreateReportCharacteristicAsync(service, 0x05);
        _bootKeyboardInput = await CreateCharacteristicAsync(service,
            BootKeyboardInputUuid,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            [0, 0, 0, 0, 0, 0, 0, 0]);
        _bootMouseInput = await CreateCharacteristicAsync(service,
            BootMouseInputUuid,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            [0, 0, 0]);
        _keyboardReport.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => GetLastReport(1, [0, 0, 0, 0, 0, 0, 0, 0]));
        _consumerReport.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => GetLastReport(4, [0, 0]));
        _navigationReport.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => GetLastReport(5, [0, 0]));
        _mouseReport.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => GetLastReport(2, [0, 0, 0, 0, 0, 0]));
        _bootKeyboardInput.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => GetLastReport(1, [0, 0, 0, 0, 0, 0, 0, 0]));
        _bootMouseInput.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => ToBootMouseReport(GetLastReport(2, [0, 0, 0, 0, 0, 0])));
        _mouseReport.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _keyboardReport.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _consumerReport.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _navigationReport.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _bootMouseInput.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _bootKeyboardInput.SubscribedClientsChanged += OnSubscribedClientsChanged;
    }

    private static async Task<GattLocalCharacteristic> CreateCharacteristicAsync(
        GattLocalService service, Guid uuid, GattCharacteristicProperties properties,
        byte[] value)
    {
        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = properties,
            StaticValue = CryptographicBuffer.CreateFromByteArray(value),
            ReadProtectionLevel = properties.HasFlag(GattCharacteristicProperties.Read)
                ? GattProtectionLevel.EncryptionRequired : GattProtectionLevel.Plain,
            WriteProtectionLevel = properties.HasFlag(GattCharacteristicProperties.Write) ||
                properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                ? GattProtectionLevel.EncryptionRequired : GattProtectionLevel.Plain,
        };
        var result = await service.CreateCharacteristicAsync(uuid, parameters);
        if (result.Error != BluetoothError.Success || result.Characteristic is null)
            throw new InvalidOperationException($"Could not create HID characteristic {uuid}: {result.Error}");
        return result.Characteristic;
    }

    private static async Task<GattLocalCharacteristic> CreateReportCharacteristicAsync(
        GattLocalService service, byte reportId)
    {
        var characteristic = await CreateCharacteristicAsync(service, ReportUuid,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            reportId == 1 ? [0, 0, 0, 0, 0, 0, 0, 0] :
                reportId is 4 or 5 ? [0, 0] : [0, 0, 0, 0, 0, 0]);
        var descriptor = new GattLocalDescriptorParameters
        {
            StaticValue = CryptographicBuffer.CreateFromByteArray([reportId, 0x01]),
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
        };
        var result = await characteristic.CreateDescriptorAsync(ReportReferenceUuid, descriptor);
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Could not create HID report descriptor: {result.Error}");
        return characteristic;
    }

    private async Task<bool> SendReportAsync(byte reportId, byte[] report,
        string? expectedTargetDeviceUdid = null, int? expectedGeneration = null)
    {
        GattLocalCharacteristic? characteristic = null;
        GattSubscribedClient? target = null;
        byte[]? payload = null;
        string? routeDeviceUdid = null;
        string? routeClientId = null;
        var routeGeneration = 0;
        await _targetClientGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsAdvertising) return false;
            routeDeviceUdid = _targetDeviceUdid;
            if (string.IsNullOrWhiteSpace(routeDeviceUdid)) return false;
            if (expectedTargetDeviceUdid is not null &&
                (!string.Equals(routeDeviceUdid, expectedTargetDeviceUdid,
                    StringComparison.OrdinalIgnoreCase) ||
                 expectedGeneration != Volatile.Read(ref _routeGeneration)))
                return false;
            routeGeneration = Volatile.Read(ref _routeGeneration);
            _lastReports[reportId] = report;
            if (reportId == 2)
            {
                characteristic = SelectTargetCharacteristic(_mouseReport,
                    _bootMouseInput);
                target = FindTargetSubscriber(characteristic);
                if (target is null) return false;
                var state = GetClientState(target.Session.DeviceId.Id);
                if (state.ProtocolMode == 0 &&
                    ReferenceEquals(characteristic, _mouseReport) &&
                    HasTargetSubscriber(_bootMouseInput))
                {
                    characteristic = _bootMouseInput;
                    target = FindTargetSubscriber(characteristic);
                }
                payload = ReferenceEquals(characteristic, _bootMouseInput)
                    ? ToBootMouseReport(report) : report;
            }
            else if (reportId == 1)
            {
                characteristic = SelectTargetCharacteristic(_keyboardReport,
                    _bootKeyboardInput);
                target = FindTargetSubscriber(characteristic);
                if (target is null) return false;
                var state = GetClientState(target.Session.DeviceId.Id);
                if (state.ProtocolMode == 0 && ReferenceEquals(characteristic, _keyboardReport) &&
                    HasTargetSubscriber(_bootKeyboardInput))
                {
                    characteristic = _bootKeyboardInput;
                    target = FindTargetSubscriber(characteristic);
                }
                payload = report;
            }
            else if (reportId == 4)
            {
                characteristic = _consumerReport;
                target = FindTargetSubscriber(characteristic);
                if (target is null) return false;
                payload = report;
            }
            else if (reportId == 5)
            {
                characteristic = _navigationReport;
                target = FindTargetSubscriber(characteristic);
                if (target is null) return false;
                payload = report;
            }
            else return false;
            routeClientId = target!.Session.DeviceId.Id;
        }
        finally
        {
            _targetClientGate.Release();
        }
        return await NotifyReportAsync(reportId, characteristic, payload!, target!,
            routeDeviceUdid!, routeGeneration, routeClientId!,
            reportId == 2 ? MouseNotificationTimeout : NotificationTimeout)
            .ConfigureAwait(false);
    }

    private bool IsCurrentRoute(string targetDeviceUdid, int generation) =>
        Volatile.Read(ref _routeGeneration) == generation &&
        string.Equals(_targetDeviceUdid, targetDeviceUdid,
            StringComparison.OrdinalIgnoreCase) && IsAdvertising;

    private async Task<bool> NotifyReportAsync(byte reportId,
        GattLocalCharacteristic? characteristic,
        byte[] report, GattSubscribedClient targetClient, string expectedDeviceUdid,
        int expectedGeneration, string expectedClientId, TimeSpan notificationTimeout)
    {
        if (characteristic is null ||
            !IsCurrentRouteForClient(expectedDeviceUdid, expectedGeneration,
                expectedClientId)) return false;
        NotificationChannel channel;
        var isMouse = reportId == 2;
        var transportGate = isMouse ? _mouseNotificationTransportGate : null;
        lock (_notificationChannelSync)
        {
            channel = isMouse ? _mouseNotificationChannel : _notificationChannel;
            channel.Enter();
        }
        try
        {
            if (!IsCurrentRouteForClient(expectedDeviceUdid, expectedGeneration,
                    expectedClientId)) return false;
            var buffer = CryptographicBuffer.CreateFromByteArray(report);
            bool acquired;
            if (isMouse)
            {
                // Never wait behind an older mouse notification. If its
                // native WinRT operation is still alive, this sample is
                // disposable and must be dropped immediately.
                acquired = await transportGate!.WaitAsync(TimeSpan.Zero)
                    .ConfigureAwait(false);
            }
            else
            {
                try
                {
                    acquired = await channel.Gate.WaitAsync(notificationTimeout,
                        channel.Cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
            if (!acquired)
            {
                if (!isMouse)
                    HandleNotificationFailure(reportId, channel,
                        new TimeoutException("The Bluetooth HID notification gate timed out."));
                return false;
            }
            var gateReleased = false;
            try
            {
                if (!IsCurrentRouteForClient(expectedDeviceUdid, expectedGeneration,
                    expectedClientId)) return false;
                if (isMouse)
                {
                    // Do not pass a cancellation token to the native operation:
                    // AsTask(token) can complete its wrapper while the WinRT
                    // GATT call is still in flight. Track the real operation
                    // and release the single mouse slot only when it ends.
                    var notifyTask = characteristic.NotifyValueAsync(buffer, targetClient)
                        .AsTask();
                    var completed = await Task.WhenAny(notifyTask,
                        Task.Delay(notificationTimeout)).ConfigureAwait(false);
                    if (completed != notifyTask)
                    {
                        HandleNotificationFailure(reportId, channel,
                            new TimeoutException("The Bluetooth HID notification timed out."));
                        _ = ReleaseMouseNotificationGateAsync(notifyTask, transportGate!);
                        gateReleased = true;
                        return false;
                    }
                    await notifyTask.ConfigureAwait(false);
                    return true;
                }
                using var notificationCts = CancellationTokenSource.CreateLinkedTokenSource(
                    channel.Cancellation.Token);
                notificationCts.CancelAfter(notificationTimeout);
                await characteristic.NotifyValueAsync(buffer, targetClient)
                    .AsTask(notificationCts.Token).ConfigureAwait(false);
                return true;
            }
            finally
            {
                if (!gateReleased)
                {
                    if (isMouse) transportGate!.Release();
                    else channel.Gate.Release();
                }
            }
        }
        catch (Exception error)
        {
            if (!channel.Cancellation.IsCancellationRequested)
                HandleNotificationFailure(reportId, channel, error);
            return false;
        }
        finally { channel.Exit(); }
    }

    private static async Task ReleaseMouseNotificationGateAsync(Task notifyTask,
        SemaphoreSlim gate)
    {
        try { await notifyTask.ConfigureAwait(false); }
        catch (Exception) { }
        finally
        {
            try { gate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    private void HandleNotificationFailure(byte reportId, NotificationChannel channel,
        Exception error)
    {
        if (reportId == 2)
        {
            // A mouse packet is disposable latest-wins state. Windows can
            // temporarily hold NotifyValueAsync while the iPhone is draining
            // its HID interval; retire only this notification slot and drop
            // the stale packet. Never turn a transient mouse stall into a
            // transport disconnect or a user-facing startup failure.
            RetireNotificationChannel(channel);
            return;
        }
        MarkNotificationFailure(channel, error);
    }

    private bool IsCurrentRouteForClient(string deviceUdid, int generation,
        string clientId) => IsCurrentRoute(deviceUdid, generation) &&
        string.Equals(Volatile.Read(ref _targetClientId), clientId,
            StringComparison.OrdinalIgnoreCase);

    private void MarkNotificationFailure(NotificationChannel channel,
        Exception error)
    {
        RetireNotificationChannel(channel);
        Interlocked.Exchange(ref _transportFailed, 1);
        SetStatus("Bluetooth HID control disconnected.", error.Message);
    }

    private void AdvanceRouteGeneration()
    {
        Interlocked.Increment(ref _routeGeneration);
        RetireNotificationChannel();
    }

    private void RetireNotificationChannel(NotificationChannel? expected = null)
    {
        NotificationChannel[] retired;
        lock (_notificationChannelSync)
        {
            if (expected is not null && ReferenceEquals(_notificationChannel, expected))
            {
                retired = [_notificationChannel];
                _notificationChannel = new NotificationChannel();
            }
            else if (expected is not null && ReferenceEquals(_mouseNotificationChannel, expected))
            {
                retired = [_mouseNotificationChannel];
                _mouseNotificationChannel = new NotificationChannel();
            }
            else if (expected is not null)
            {
                retired = [expected];
            }
            else
            {
                retired = [_notificationChannel, _mouseNotificationChannel];
                _notificationChannel = new NotificationChannel();
                _mouseNotificationChannel = new NotificationChannel();
            }
        }
        foreach (var channel in retired) channel.Retire();
    }

    private byte[] GetLastReport(byte reportId, byte[] fallback) =>
        _lastReports.TryGetValue(reportId, out var report) ? report : fallback;

    private static byte[] ToBootMouseReport(byte[] report)
    {
        if (report.Length < 5) return [0, 0, 0];
        var x = (short)(report[1] | report[2] << 8);
        var y = (short)(report[3] | report[4] << 8);
        return
        [
            (byte)(report[0] & 0x07),
            unchecked((byte)(sbyte)Math.Clamp(x, sbyte.MinValue, sbyte.MaxValue)),
            unchecked((byte)(sbyte)Math.Clamp(y, sbyte.MinValue, sbyte.MaxValue)),
        ];
    }

    private void StopAndClearProviderState()
    {
        Interlocked.Exchange(ref _advertisingStartupInProgress, 0);
        AdvanceRouteGeneration();
        var provider = _provider;
        BeginStopAdvertisingSession();
        _provider = null;
        if (provider is not null)
        {
            provider.AdvertisementStatusChanged -= OnAdvertisementStatusChanged;
        }
        _mouseReport = null;
        _keyboardReport = null;
        _consumerReport = null;
        _navigationReport = null;
        _bootMouseInput = null;
        _bootKeyboardInput = null;
        _protocolModeCharacteristic = null;
        _wheelResolutionCharacteristic = null;
        DetachClientSessions();
        _clientStates.Clear();
        _clientRoutes.Clear();
        Volatile.Write(ref _targetClientId, null);
        _targetDeviceUdid = null;
    }

    private async Task<bool> StopAdvertisingSessionAsync()
    {
        var stopped = BeginStopAdvertisingSession();
        if (stopped is null) return true;
        try
        {
            var completed = await Task.WhenAny(stopped.Task,
                Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            return completed == stopped.Task && await stopped.Task.ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_advertisingStopped, stopped))
                _advertisingStopped = null;
        }
    }

    private TaskCompletionSource<bool>? BeginStopAdvertisingSession()
    {
        AdvanceRouteGeneration();
        Interlocked.Exchange(ref _advertisingStopRequested, 1);
        _advertisingStarted?.TrySetResult(false);
        _advertisingStarted = null;
        _advertisingStopped?.TrySetResult(false);
        _advertisingStopped = null;
        _clientConnected?.TrySetResult(false);
        _clientConnected = null;
        _clientRoutes.EndTarget();
        Volatile.Write(ref _targetClientId, null);
        _targetDeviceUdid = null;
        _targetDeviceName = null;
        _preferredClientId = null;
        Interlocked.Exchange(ref _transportFailed, 0);
        _lastReports.Clear();
        if (!IsAdvertising) return null;
        var stopped = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _advertisingStopped = stopped;
        try { _provider?.StopAdvertising(); }
        catch
        {
            stopped.TrySetResult(false);
        }
        return stopped;
    }

    private void OnAdvertisementStatusChanged(GattServiceProvider sender,
        GattServiceProviderAdvertisementStatusChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _provider)) return;
        if (args.Status == GattServiceProviderAdvertisementStatus.Started ||
            args.Status == GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData)
        {
            if (Volatile.Read(ref _advertisingStopRequested) != 0) return;
            Interlocked.Exchange(ref _transportFailed, 0);
            _advertisingStarted?.TrySetResult(true);
            SetStatus($"Bluetooth HID control is advertising ({args.Status}).", null);
        }
        else if (args.Status is GattServiceProviderAdvertisementStatus.Stopped or
                 GattServiceProviderAdvertisementStatus.Aborted)
        {
            if (Volatile.Read(ref _advertisingStopRequested) != 0)
            {
                _advertisingStopped?.TrySetResult(true);
                return;
            }
            // MediaTek/Windows can report an Aborted/Success transition while
            // replacing the advertisement and then report Started immediately
            // afterwards. Do not poison a startup that is still in progress.
            if (Volatile.Read(ref _advertisingStartupInProgress) != 0)
                return;
            if (IsAdvertising) return;
            Interlocked.Exchange(ref _transportFailed, 1);
            SetStatus("Bluetooth HID control stopped unexpectedly.", args.Error.ToString());
        }
    }

    private void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args)
    {
        // WinRT invokes this callback on its GATT callback thread. Reading
        // SubscribedClients or SessionStatus from that thread can fail with
        // RPC_E_CANTCALLOUT_ININPUTSYNCCALL and can stall the WPF dispatcher.
        // Enter the refresh from a pool thread before touching any WinRT
        // collection or session object.
        TrackClientRefresh(() => Task.Run(() => RefreshSubscribedClientsAsync(sender)));
    }

    private async Task RefreshSubscribedClientsAsync(GattLocalCharacteristic sender)
    {
        await _clientRefreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            TrackSubscribedClients();
            await RefreshTargetClientAsync().ConfigureAwait(false);
            if (!IsCurrentCharacteristic(sender)) return;
            if (HasTargetInputSubscriber())
                Interlocked.Exchange(ref _transportFailed, 0);
            var connected = IsConnected;
            if (IsMouseConnected) _clientConnected?.TrySetResult(true);
            SetStatus(connected
                    ? $"Selected iPhone/iPad subscribed to the {GetReportName(sender)}."
                    : HasAnySubscriber
                        ? Volatile.Read(ref _targetClientId) is null
                            ? "Multiple Bluetooth clients are connected; waiting for the selected iPhone/iPad."
                            : "The selected Bluetooth client is connected; waiting for its HID mouse report."
                        : "Bluetooth HID control is advertising; waiting for a client.",
                null);
            // Do not probe a freshly subscribed iPhone with synthetic zero
            // notifications. iOS/Windows can still be completing the HID
            // subscription when the callback arrives; a probe at that point
            // is reported as a transport failure and aborts an otherwise
            // healthy connection. The first real input report is the probe.
        }
        catch (Exception error)
        {
            SetStatus("Could not identify the selected Bluetooth client.", error.Message);
        }
        finally
        {
            _clientRefreshGate.Release();
        }
    }

    private static bool HasSubscribers(GattLocalCharacteristic? characteristic)
    {
        try
        {
            return characteristic?.SubscribedClients?.Count > 0;
        }
        catch (Exception)
        {
            // WinRT may reject a synchronous collection read while the
            // Bluetooth service is processing a callback.
            return false;
        }
    }

    private bool IsCurrentCharacteristic(GattLocalCharacteristic characteristic) =>
        ReferenceEquals(characteristic, _mouseReport) ||
        ReferenceEquals(characteristic, _keyboardReport) ||
        ReferenceEquals(characteristic, _consumerReport) ||
        ReferenceEquals(characteristic, _navigationReport) ||
        ReferenceEquals(characteristic, _bootMouseInput) ||
        ReferenceEquals(characteristic, _bootKeyboardInput);

    private bool HasTargetSubscriber(GattLocalCharacteristic? characteristic) =>
        FindTargetSubscriber(characteristic) is not null;

    private bool HasTargetInputSubscriber() => HasTargetSubscriber(_mouseReport) ||
        HasTargetSubscriber(_keyboardReport) || HasTargetSubscriber(_bootMouseInput) ||
        HasTargetSubscriber(_bootKeyboardInput) || HasTargetSubscriber(_consumerReport) ||
        HasTargetSubscriber(_navigationReport);

    private GattSubscribedClient? FindTargetSubscriber(
        GattLocalCharacteristic? characteristic)
    {
        var targetClientId = Volatile.Read(ref _targetClientId);
        if (characteristic is null || string.IsNullOrWhiteSpace(targetClientId))
            return null;
        try
        {
            return characteristic.SubscribedClients.FirstOrDefault(client =>
                string.Equals(client.Session.DeviceId.Id, targetClientId,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private GattLocalCharacteristic? SelectTargetCharacteristic(
        GattLocalCharacteristic? reportCharacteristic,
        GattLocalCharacteristic? bootCharacteristic)
    {
        // The report and boot characteristics each have their own
        // GattSubscribedClient object. Select the characteristic first, then
        // pass that characteristic's client to NotifyValueAsync; passing a
        // client obtained from another characteristic can fail on Windows.
        if (FindTargetSubscriber(reportCharacteristic) is not null)
            return reportCharacteristic;
        if (FindTargetSubscriber(bootCharacteristic) is not null)
            return bootCharacteristic;
        return null;
    }

    private async Task RefreshTargetClientAsync()
    {
        string? targetName;
        int generation;
        string[] clientIds;
        await _targetClientGate.WaitAsync().ConfigureAwait(false);
        try
        {
            generation = Volatile.Read(ref _routeGeneration);
            targetName = _targetDeviceName;
            clientIds = EnumerateSubscribedClients()
                .Select(client => client.Session.DeviceId.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _targetClientGate.Release();
        }

        var clients = new List<(string Id, string Name)>(clientIds.Length);
        foreach (var clientId in clientIds)
            clients.Add((clientId, await GetClientNameAsync(clientId).ConfigureAwait(false)));

        await _targetClientGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != Volatile.Read(ref _routeGeneration)) return;
            Volatile.Write(ref _targetClientId,
                _clientRoutes.Refresh(clients, targetName, _preferredClientId));
        }
        finally
        {
            _targetClientGate.Release();
        }
    }

    private async Task BeginTargetRouteAsync(string targetDeviceUdid,
        string? targetDeviceName = null,
        bool clearPreviousBinding = false,
        string? preferredClientId = null)
    {
        await _targetClientGate.WaitAsync().ConfigureAwait(false);
        try
        {
            AdvanceRouteGeneration();
            var clientIds = EnumerateSubscribedClients()
                .Select(client => client.Session.DeviceId.Id)
                .ToArray();
            _clientRoutes.BeginTarget(targetDeviceUdid, clientIds, clearPreviousBinding);
            _targetDeviceUdid = targetDeviceUdid;
            _targetDeviceName = targetDeviceName;
            _preferredClientId = preferredClientId;
            Volatile.Write(ref _targetClientId,
                _clientRoutes.Refresh(clientIds.Select(id => (id, string.Empty)),
                    targetDeviceName, preferredClientId));
        }
        finally
        {
            _targetClientGate.Release();
        }
    }

    private static async Task<string> GetClientNameAsync(string clientId)
    {
        try
        {
            using var device = await BluetoothLEDevice.FromIdAsync(clientId);
            if (!string.IsNullOrWhiteSpace(device?.Name)) return device.Name;
        }
        catch { }
        try
        {
            var information = await DeviceInformation.CreateFromIdAsync(clientId);
            return information?.Name ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private IEnumerable<GattSubscribedClient> EnumerateSubscribedClients()
    {
        foreach (var characteristic in new[]
                 { _mouseReport, _keyboardReport, _consumerReport,
                   _navigationReport,
                   _bootMouseInput, _bootKeyboardInput })
        {
            if (characteristic is null) continue;
            foreach (var client in characteristic.SubscribedClients)
                yield return client;
        }
    }

    private void TrackSubscribedClients()
    {
        foreach (var client in EnumerateSubscribedClients())
        {
            var id = client.Session.DeviceId.Id;
            if (string.IsNullOrWhiteSpace(id)) continue;
            _ = GetClientState(id);
            if (_clientSessions.TryGetValue(id, out var existing) &&
                ReferenceEquals(existing, client.Session))
                continue;
            if (existing is not null)
            {
                existing.SessionStatusChanged -= OnGattSessionStatusChanged;
                _clientSessions.TryRemove(id, out _);
            }
            _clientSessions[id] = client.Session;
            _clientConnectedAt[id] = DateTimeOffset.Now;
            client.Session.SessionStatusChanged += OnGattSessionStatusChanged;
        }
    }

    private void OnGattSessionStatusChanged(GattSession sender,
        GattSessionStatusChangedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            args.Status != GattSessionStatus.Closed) return;
        // SessionStatusChanged is also a WinRT callback. Defer before
        // enumerating sessions and subscribed clients.
        TrackClientRefresh(() => Task.Run(RefreshClosedSessionAsync));
    }

    private void TrackClientRefresh(Func<Task> factory)
    {
        lock (_clientRefreshTrackingSync)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            var task = factory();
            if (task.IsCompleted)
                return;
            _clientRefreshTasks.TryAdd(task, 0);
            _ = task.ContinueWith(completed =>
            {
                lock (_clientRefreshTrackingSync)
                    _clientRefreshTasks.TryRemove(completed, out _);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task DrainClientRefreshesAsync()
    {
        while (!_clientRefreshTasks.IsEmpty)
            await Task.WhenAll(_clientRefreshTasks.Keys.ToArray()).ConfigureAwait(false);
    }

    private async Task RefreshClosedSessionAsync()
    {
        await _clientRefreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var pair in _clientSessions.ToArray())
            {
                if (pair.Value.SessionStatus == GattSessionStatus.Closed &&
                    _clientSessions.TryRemove(pair.Key, out var removed))
                {
                    _clientConnectedAt.TryRemove(pair.Key, out _);
                    removed.SessionStatusChanged -= OnGattSessionStatusChanged;
                }
            }
            TrackSubscribedClients();
            await RefreshTargetClientAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) == 0)
            {
                SetStatus(IsConnected
                        ? "A selected Bluetooth HID client is connected."
                        : HasAnySubscriber
                            ? "Bluetooth clients are connected; waiting for the selected iPhone/iPad."
                            : "Bluetooth HID control is advertising; waiting for a client.",
                    null);
                if (!IsConnected) _clientConnected?.TrySetResult(false);
            }
        }
        catch (Exception error)
        {
            SetStatus("Could not refresh Bluetooth clients after disconnect.", error.Message);
        }
        finally
        {
            _clientRefreshGate.Release();
        }
    }

    private void DetachClientSessions()
    {
        foreach (var session in _clientSessions.Values)
            session.SessionStatusChanged -= OnGattSessionStatusChanged;
        _clientSessions.Clear();
    }

    private ClientState GetClientState(string clientId) =>
        _clientStates.GetOrAdd(clientId, static _ => new ClientState());

    private ClientState? GetTargetClientState()
    {
        var targetId = Volatile.Read(ref _targetClientId);
        return string.IsNullOrWhiteSpace(targetId) ? null :
            _clientStates.TryGetValue(targetId, out var state) ? state : null;
    }

    private byte[] GetProtocolModeValue(GattReadRequestedEventArgs args)
    {
        var id = args.Session?.DeviceId?.Id;
        return string.IsNullOrWhiteSpace(id)
            ? DefaultProtocolMode.ToArray()
            : [GetClientState(id).ProtocolMode];
    }

    private string GetReportName(GattLocalCharacteristic characteristic) =>
        ReferenceEquals(characteristic, _mouseReport) ? "HID mouse report" :
        ReferenceEquals(characteristic, _bootMouseInput) ? "HID boot mouse report" :
        ReferenceEquals(characteristic, _bootKeyboardInput) ? "HID boot keyboard report" :
        ReferenceEquals(characteristic, _consumerReport) ? "HID consumer-control report" :
        ReferenceEquals(characteristic, _navigationReport) ? "HID navigation-control report" :
        "HID keyboard report";

    private void OnProtocolModeWriteRequested(GattLocalCharacteristic sender,
        GattWriteRequestedEventArgs args) => _ = RunGattCallbackAsync(
        () => args.GetDeferral(), async () =>
        {
            var request = await args.GetRequestAsync();
            if (request is not null)
            {
                using var reader = DataReader.FromBuffer(request.Value);
                if (request.Value.Length > 0)
                {
                    var mode = reader.ReadByte();
                    if (mode is 0x00 or 0x01)
                    {
                        var id = args.Session?.DeviceId?.Id;
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            GetClientState(id).ProtocolMode = mode;
                            if (string.Equals(id, Volatile.Read(ref _targetClientId),
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                // The first real input report is sent on
                                // demand. Do not inject a synthetic report
                                // while iOS is still switching protocol mode.
                            }
                        }
                    }
                }
                request.Respond();
            }
        }, "protocol_mode_write");

    private void OnWheelResolutionWriteRequested(GattLocalCharacteristic sender,
        GattWriteRequestedEventArgs args) => _ = RunGattCallbackAsync(
        () => args.GetDeferral(), async () =>
        {
            var request = await args.GetRequestAsync();
            if (request is not null)
            {
                using var reader = DataReader.FromBuffer(request.Value);
                if (request.Value.Length > 0)
                {
                    var multiplier = (byte)Math.Clamp((int)reader.ReadByte(), 1, 10);
                    var id = args.Session?.DeviceId?.Id;
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        GetClientState(id).WheelResolutionMultiplier = multiplier;
                        if (string.Equals(id, Volatile.Read(ref _targetClientId),
                                StringComparison.OrdinalIgnoreCase))
                            SetStatus($"HID wheel resolution multiplier set to {multiplier}.", null);
                    }
                }
                request.Respond();
            }
        }, "wheel_resolution_write");

    private async Task<GattLocalCharacteristic> CreateFeatureCharacteristicAsync(
        GattLocalService service)
    {
        var characteristic = await CreateCharacteristicAsync(service, ReportUuid,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Write |
                GattCharacteristicProperties.WriteWithoutResponse,
            [1]);
        var descriptor = new GattLocalDescriptorParameters
        {
            StaticValue = CryptographicBuffer.CreateFromByteArray([3, 0x02]),
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
        };
        var result = await characteristic.CreateDescriptorAsync(ReportReferenceUuid, descriptor);
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Could not create HID wheel feature descriptor: {result.Error}");
        characteristic.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => GetWheelResolutionValue(args));
        characteristic.WriteRequested += OnWheelResolutionWriteRequested;
        return characteristic;
    }

    private byte[] GetWheelResolutionValue(GattReadRequestedEventArgs args)
    {
        var id = args.Session?.DeviceId?.Id;
        return string.IsNullOrWhiteSpace(id) ? [1] :
            [GetClientState(id).WheelResolutionMultiplier];
    }

    private static void OnControlPointWriteRequested(GattLocalCharacteristic sender,
        GattWriteRequestedEventArgs args) => _ = RunGattCallbackAsync(
        () => args.GetDeferral(), async () =>
        {
            var request = await args.GetRequestAsync();
            request?.Respond();
        }, "control_point_write");

    private static void RespondToReadAsync(GattReadRequestedEventArgs args,
        Func<byte[]> valueFactory) => _ = RunGattCallbackAsync(
        () => args.GetDeferral(), async () =>
        {
            var request = await args.GetRequestAsync();
            request?.RespondWithValue(CryptographicBuffer.CreateFromByteArray(valueFactory()));
        }, "characteristic_read");

    private static async Task RunGattCallbackAsync(Func<Deferral> getDeferral,
        Func<Task> callback, string operation)
    {
        Deferral? deferral = null;
        try
        {
            deferral = getDeferral();
            await callback().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            LogGattCallbackFailure(operation, error);
        }
        finally
        {
            if (deferral is not null)
            {
                try { deferral.Complete(); }
                catch (Exception error)
                {
                    LogGattCallbackFailure(operation + "_complete", error);
                }
            }
        }
    }

    private static void LogGattCallbackFailure(string operation, Exception error) =>
        DiagnosticLogger.ExceptionOnce(
            $"bluetooth-hid-gatt-{operation}-{error.GetType().FullName}",
            "bluetooth", "gatt_callback_failed", error,
            ("operation", operation));

    private void SetStatus(string status, string? error)
    {
        Status = status;
        Error = error;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_clientRefreshTrackingSync)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Volatile.Write(ref _disposed, 1);
        }
        await StopAsync().ConfigureAwait(false);
        await DrainClientRefreshesAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { StopAndClearProviderState(); }
        finally { _gate.Release(); }
        RetireNotificationChannel();
        _clientRefreshGate.Dispose();
        _targetClientGate.Dispose();
        _mouseNotificationTransportGate.Dispose();
        _gate.Dispose();
    }

    private sealed class ClientState
    {
        public byte ProtocolMode = 0x01;
        public byte WheelResolutionMultiplier = 1;
    }

    private sealed class NotificationChannel
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal CancellationTokenSource Cancellation { get; } = new();
        private int _active;
        private int _retired;
        private int _disposed;

        internal void Enter() => Interlocked.Increment(ref _active);

        internal void Exit()
        {
            if (Interlocked.Decrement(ref _active) == 0)
                DisposeWhenIdle();
        }

        internal void Retire()
        {
            if (Interlocked.Exchange(ref _retired, 1) == 0)
            {
                try { Cancellation.Cancel(); }
                catch (Exception) { }
            }
            DisposeWhenIdle();
        }

        private void DisposeWhenIdle()
        {
            if (Volatile.Read(ref _retired) == 0 ||
                Volatile.Read(ref _active) != 0 ||
                Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Gate.Dispose();
            Cancellation.Dispose();
        }
    }
}
