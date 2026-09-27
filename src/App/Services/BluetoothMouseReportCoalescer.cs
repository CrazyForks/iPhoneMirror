namespace IPhoneMirror.App.Services;

/// <summary>Retains one bounded unsent relative-motion report.</summary>
internal static class BluetoothMouseReportCoalescer
{
    internal const int ReportLength = 6;

    internal static byte[] MergePendingMotion(byte[]? pending, byte[] incoming)
    {
        // The HID pump uses this newest-wins helper for unsent relative
        // motion. A pending report is disposable once it is older than the
        // current BLE send window.
        return incoming.Length == ReportLength ? incoming : pending ?? incoming;
    }

    internal static byte[] MergeRecentMotion(byte[]? pending, byte[] incoming)
    {
        if (pending is null || pending.Length != ReportLength ||
            incoming.Length != ReportLength)
            return incoming;

        static short ReadAxis(byte[] report, int offset) =>
            (short)(report[offset] | report[offset + 1] << 8);
        static short AddAxis(short left, short right) => (short)Math.Clamp(
            (int)left + right, short.MinValue + 1, short.MaxValue);

        var x = AddAxis(ReadAxis(pending, 1), ReadAxis(incoming, 1));
        var y = AddAxis(ReadAxis(pending, 3), ReadAxis(incoming, 3));
        return
        [
            incoming[0],
            (byte)(x & 0xFF), (byte)((x >> 8) & 0xFF),
            (byte)(y & 0xFF), (byte)((y >> 8) & 0xFF),
            incoming[5],
        ];
    }
}
