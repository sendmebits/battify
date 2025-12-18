# Battify - AI Coding Agent Instructions

## Project Overview
Battify is a lightweight Windows system tray application for monitoring Bluetooth device battery levels. It's a WinForms app targeting .NET 8.0 with Windows 10/11 Runtime APIs.

## Architecture

### Core Components
- **[MainForm.cs](../MainForm.cs)** - Central orchestrator: system tray, device discovery, battery monitoring, notifications
- **[Settings.cs](../Settings.cs)** - JSON-based persistence to `%AppData%\Battify\settings.json`
- **[SettingsForm.cs](../SettingsForm.cs)** - User configuration UI
- **[ModernTheme.cs](../ModernTheme.cs)** - Windows 11-style UI components (custom buttons, panels, colors)

### Battery Monitoring Architecture (Two-Tier System)
1. **Tier 1 - DeviceWatcher**: Event-driven device discovery with 5-second throttling (`WATCHER_THROTTLE_SECONDS`)
2. **Tier 2 - Timer**: Periodic safety net using `DeviceScanIntervalMinutes` (default: 10 min)
3. **Slow Loop**: Forced GATT reads at `BatteryUpdateIntervalMinutes` (default: 90 min) to reduce Bluetooth traffic

### Key UUIDs
```csharp
BATTERY_SERVICE_UUID = "0000180F-0000-1000-8000-00805F9B34FB"
BATTERY_LEVEL_CHARACTERISTIC_UUID = "00002A19-0000-1000-8000-00805F9B34FB"
```

## Build & Run

```cmd
# Build standalone (self-contained, no .NET required on target)
build-standalone.bat

# Output: Battify-Standalone\Battify.exe
.\Battify-Standalone\Battify.exe
```

The build script cleans `bin/` and `obj/` after publishing.

## Code Patterns

### Resource Management
**Critical**: Dispose GDI handles and Bluetooth resources to prevent leaks:
```csharp
// Icon cleanup pattern - use NativeMethods.DestroyIcon()
IntPtr hIcon = bitmap.GetHicon();
Icon clonedIcon = (Icon)icon.Clone();
NativeMethods.DestroyIcon(hIcon);

// GATT service disposal in finally blocks
batteryService?.Dispose();
```

### Async Device Enumeration
Use `BluetoothLEDevice.FromIdAsync()` and `GetGattServicesAsync(BluetoothCacheMode.Uncached)` for fresh reads. Cached reads use property key `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2`.

### UI Thread Safety
DeviceWatcher events come from background threads - always marshal to UI:
```csharp
if (this.InvokeRequired)
    this.BeginInvoke(new Action(async () => await CheckBluetoothDevices()));
```

### Settings Persistence
Only save when values actually change to reduce disk I/O. Settings support backward compatibility via synced properties (`CheckIntervalMinutes` ↔ `BatteryUpdateIntervalMinutes`).

## Project Conventions
- **Logging**: Conditional via `LoggingEnabled` setting, outputs to `%TEMP%\Battify_Debug.log`
- **Appearance**: Use `ModernTheme` static class for Windows 11-style UI
- **Icons**: Assets in `Assets/` folder, copied to output on build
- **Target**: Windows 10 SDK 19041 minimum (`net8.0-windows10.0.19041.0`)

## Testing Notes
- Test with actual Bluetooth LE devices supporting GATT Battery Service
- Not all devices report battery (check debug log for "No Battery Service found")
- Use logging toggle in settings for debugging device enumeration issues


## Agent Instructions
When the task requires multiple steps or non-trivial changes, present a detailed plan using #planReview and wait for approval before executing.
If the plan is rejected, incorporate the comments and submit an updated plan with #planReview.
When the user asks for a step-by-step guide or walkthrough, present it using #walkthroughReview.
Always use #askUser before completing any task to confirm the result matches what the user asked for.