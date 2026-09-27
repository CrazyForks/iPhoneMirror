using System.Diagnostics;
using IPhoneMirror.App.Services;

const string targetUdid = "00008101-00044D600A22001E";
const string targetName = "Ray's Phone";
var duration = args.Length > 0 && int.TryParse(args[0], out var seconds)
    ? TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 120))
    : TimeSpan.FromSeconds(20);

DiagnosticLogger.Initialize();
await using var hid = new BluetoothHidMouseService();
hid.StatusChanged += (_, _) => Console.WriteLine(
    $"STATUS advertising={hid.IsAdvertising} connected={hid.IsConnected} " +
    $"failed={hid.HasTransportFailure} text={hid.Status} error={hid.Error ?? "none"}");

Console.WriteLine("Starting BLE HID advertising...");
if (!await hid.StartAsync(targetUdid, targetName))
{
    Console.Error.WriteLine($"START_FAILED {hid.Status}: {hid.Error}");
    return 2;
}

Console.WriteLine("Waiting up to 30 seconds for the paired iPhone HID subscription...");
if (!await hid.WaitForConnectionAsync(TimeSpan.FromSeconds(30)))
{
    Console.Error.WriteLine("CONNECT_TIMEOUT");
    return 3;
}

Console.WriteLine($"CONNECTED client={hid.TargetClientId}; stress={duration.TotalSeconds:F0}s");
var started = Stopwatch.GetTimestamp();
var sent = 0;
var direction = 1;
while (Stopwatch.GetElapsedTime(started) < duration && !hid.HasTransportFailure)
{
    // Alternate direction frequently so any stale replay is immediately
    // visible as overshoot instead of blending into one long movement.
    if (sent % 18 == 0) direction = -direction;
    // Periodic discrete wheel reports exercise the priority path shared by
    // click/drag state transitions without producing unintended taps.
    var wheel = sent > 0 && sent % 120 == 0 ? 1 : 0;
    await hid.SendMouseAsync(direction * 3, 0, wheel: wheel,
        expectedTargetDeviceUdid: targetUdid);
    sent++;
    await Task.Delay(8);
}

await hid.SendMouseAsync(0, 0, expectedTargetDeviceUdid: targetUdid);
await Task.Delay(500);
Console.WriteLine($"RESULT sent={sent} connected={hid.IsConnected} " +
    $"failed={hid.HasTransportFailure} status={hid.Status} error={hid.Error ?? "none"}");
return hid.HasTransportFailure ? 4 : 0;
