namespace IPhoneMirror.App.Services;

public sealed record BluetoothClientInfo(string Id, string Name, string Address,
    DateTimeOffset ConnectedAt, string? BoundDeviceName = null);
