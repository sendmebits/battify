# <img width="32" height="32" alt="battify_icon_full" src="https://github.com/user-attachments/assets/4194eaa6-91e7-40fd-a732-a64dbfdb9a79" /> Battify - Lightweight Bluetooth Battery Level Monitoring And Notifications

A very lightweight Windows system tray application that monitors the battery levels of connected Bluetooth devices.

I was frustrated with bloated bluetooth vendor software when all I wanted was a simple low battery warning notification so I built **Battify**.

## Download

Standalone app zip file include with each version in the <a href=https://github.com/sendmebits/battify/releases>releases</a> page. Download and unzip to the folder of your choice and it will run from there. 

## Features

- **System Tray Integration**: Runs quietly in the system tray
- **Battery Monitoring**: Monitors battery levels of connected Bluetooth devices at your specified interval  
- **Low Battery Notifications**: Shows balloon notifications when device battery drops below the specified threshold
- **Visual Battery Indicator**: Tray icon changes color based on the lowest battery level among all devices
- **Quick Access Menu**: Right-click tray icon for options to view battery levels, refresh, or exit
- **Device Information**: View all connected Bluetooth devices and their battery status

### Double click to quickly view all devices:
<p align="center">
  <img src="https://github.com/user-attachments/assets/3b1fcb6e-c427-4c2e-ae4a-06e770df8559" alt="connected_devices" width="280" />
</p>

### At a glance battery level in system tray:
<p align="center">
  <img src="https://github.com/user-attachments/assets/4d573ea6-438c-4249-b62a-06d40bde91fc" alt="system_tray" width="280" />
</p>

### Customizable settings:
<p align="center">
  <img src="https://github.com/user-attachments/assets/ed8df238-f410-4048-b106-3a07f208e2ea" alt="settings" width="280" />
</p>

## Requirements

- Windows 10/11 (version 1903 or later recommended)
- Bluetooth-enabled computer
- Bluetooth devices that support battery level reporting (GATT Battery Service)

## Usage

1. **Launch**: The application starts minimized to the system tray
2. **Tray Icon**: Shows a battery indicator reflecting the lowest battery level among connected devices
3. **Right-click Menu**:
   - **Show Battery Levels**: View battery percentages with status indicators
   - **Show Connected Devices**: List all connected Bluetooth devices
   - **Refresh**: Manually refresh device list and battery levels
   - **Exit**: Close the application
4. **Double-click**: Quick access to battery levels
5. **Notifications**: Automatic low battery alerts at specified threshold

## Battery Status Indicators

- ✅ **Good** (60%+): Green battery icon
- ⚡ **Medium** (30-59%): Orange battery icon  
- ⚠️ **Low** (0-29%): Red battery icon

## Technical Details

### How It Works

The application uses Windows Runtime APIs to:
- Enumerate connected Bluetooth devices via `BluetoothDevice.GetDeviceSelector()`
- Monitor connection status changes
- Access GATT Battery Service (UUID: `0x180F`) when available
- Read Battery Level Characteristic (UUID: `0x2A19`)

### Device Compatibility

**Supported Devices:**
- Modern Bluetooth headphones and earbuds (AirPods, Sony, Bose, etc.)
- Wireless gaming controllers (Xbox, PlayStation)
- Bluetooth mice and keyboards
- Fitness trackers and smartwatches
- Any Bluetooth LE device implementing GATT Battery Service

**Note**: Older Bluetooth devices may not support battery level reporting.

## Development

### Building from Source

Requirements:
- .NET 8.0 SDK (https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- Windows 10/11 development environment

```cmd
git clone https://github.com/sendmebits/battify.git
cd battify
build-standalone.bat
Battify-Standalone\Battify.exe
```

## Troubleshooting

**No devices showing**: 
- Ensure Bluetooth is enabled
- Check that devices are paired and connected
- Some devices may not support battery reporting
- Try refreshing manually from the context menu

**Permission errors**:
- Run as administrator if needed
- Check Windows privacy settings for Bluetooth access

**Battery levels not updating**:
- Device may not support GATT Battery Service
- Try refreshing manually from the context menu
