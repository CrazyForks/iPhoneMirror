# Reverse Control UI Inventory

This inventory covers user-visible UI reached from USB, Wireless, or Bluetooth reverse-control flows. The control service is the single runtime status surface; developer-preview windows and device-profile administration remain separate tools.

| Area | Existing trigger | Unified presentation | Status |
|---|---|---|---|
| USB/Wireless prerequisites | `MainViewModel.ConfirmReverseControlPrerequisitesAsync` | Blocking `Confirmation` prompt in `ReverseControlStatusWindow` | Migrated |
| Bluetooth HID report-map change | `MainViewModel.AcknowledgeBluetoothHidReportMapChangeAsync` | Blocking `UserActionRequired` prompt | Migrated |
| USB bridge startup stages | `EnableUsbControlCoreAsync` and bridge status events | Stage/diagnostic updates in status window | Migrated |
| Wireless bridge startup stages | `EnableWirelessControlCoreAsync` and bridge status events | Stage/diagnostic updates in status window | Migrated |
| Bluetooth advertising and HID setup | `EnableBluetoothControlAsync` | Stage/diagnostic updates in status window | Migrated |
| Bluetooth pairing instructions | Bluetooth control startup when no HID client is connected | Non-blocking `UserActionRequired` prompt | Migrated |
| Bluetooth client selection | `EnsureBluetoothControlBindingAsync` | `Selection` prompt with client options in status window | Migrated |
| DDI/download/mount failures | USB/Wireless bridge error codes | Unified failed state plus technical diagnostics | Migrated |
| USB/Wireless disconnect and recovery | Bridge `error`/`terminated` events | `Recovering` state, retry count, then unified failure | Migrated |
| Bluetooth transport failure | `BluetoothHidMouseService.StatusChanged` | Unified failed state and diagnostics | Migrated |
| Stop/cleanup failure | USB/Wireless/Bluetooth stop paths | Unified failed state and diagnostics | Migrated |
| Retry/cancel/close | Status window actions | `ControlPromptResult` or operation cancellation | Migrated |

## Remaining dialog calls

The following reverse-control-looking windows are developer previews or device-profile administration, not runtime control startup prompts:

- `MainWindow.xaml.cs`: developer preview entries for Bluetooth notice and Bluetooth client binding.
- `AppPromptWindow.xaml.cs`: preview helpers for wired/wireless prerequisite and error surfaces.
- `BluetoothConnectionWindow.xaml.cs` and `BluetoothClientBindingWindow.xaml.cs`: device-profile administration launched from the binding settings UI.

The remaining `MessageBox.Show` and unrelated `AppPromptWindow` calls belong to recording, driver, update, device-profile, or application settings flows and are intentionally outside this migration.
