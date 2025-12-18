using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace Battify
{
    // Native methods for icon handle management
    internal static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr hIcon);
    }
    public partial class MainForm : Form
    {
        private NotifyIcon? trayIcon;
        private ContextMenuStrip? trayMenu;
        private System.Windows.Forms.Timer? batteryCheckTimer;
        private Dictionary<string, BluetoothLEDevice> connectedDevices;
        private Dictionary<string, int> lastKnownBatteryLevels;
        private Dictionary<string, DateTime> lastNotificationTime;
        private Dictionary<string, DateTime> lastForcedUpdate; // Track last expensive GATT read
        private Settings appSettings;
        private Icon? currentTrayIcon; // Track current icon for disposal
        private bool isCheckingDevices; // Prevent overlapping scans

        // DeviceWatcher for event-driven device discovery (Tier 1)
        private DeviceWatcher? deviceWatcher;
        private DateTime lastWatcherTriggered = DateTime.MinValue;
        private const int WATCHER_THROTTLE_SECONDS = 5; // Prevent event storms

        // Update checking
        private static readonly HttpClient httpClient = new HttpClient();
        private const string GITHUB_RELEASES_API = "https://api.github.com/repos/sendmebits/battify/releases/latest";
        private const string GITHUB_RELEASES_URL = "https://github.com/sendmebits/battify/releases";
        private string? latestVersionUrl; // Store URL to open when balloon is clicked
        private bool hasUpdateAvailable; // Track if update badge should be shown
        private string? availableVersion; // Store the available version for display

        // Public properties to allow SettingsForm to access current device data
        public Dictionary<string, BluetoothLEDevice> ConnectedDevices => connectedDevices;
        public Dictionary<string, int> LastKnownBatteryLevels => lastKnownBatteryLevels;
        private readonly Guid BATTERY_SERVICE_UUID = new Guid("0000180F-0000-1000-8000-00805F9B34FB");
        private readonly Guid BATTERY_LEVEL_CHARACTERISTIC_UUID = new Guid("00002A19-0000-1000-8000-00805F9B34FB");
        private static string logFilePath = Path.Combine(Path.GetTempPath(), "Battify_Debug.log");

        // Event to notify when devices are refreshed
        public event EventHandler? DevicesRefreshed;

        private static Settings? staticAppSettings;

        private static void Log(string message)
        {
            try
            {
                // Respect user's logging preference
                if (staticAppSettings == null || !staticAppSettings.LoggingEnabled)
                    return;

                string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                File.AppendAllText(logFilePath, logMessage + Environment.NewLine);
                Console.WriteLine(logMessage);
            }
            catch { }
        }

        public MainForm()
        {
            InitializeComponent();
            Log("=== Battify Started ===");
            Log($"Log file location: {logFilePath}");
            connectedDevices = new Dictionary<string, BluetoothLEDevice>();
            appSettings = Settings.Load();
            staticAppSettings = appSettings;
            
            // Initialize lastKnownBatteryLevels from settings
            lastKnownBatteryLevels = new Dictionary<string, int>(appSettings.LastKnownBatteryLevels);
            lastNotificationTime = new Dictionary<string, DateTime>();
            lastForcedUpdate = new Dictionary<string, DateTime>();
            
            // Hide the form initially
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            
            CreateTrayIcon();
            StartBatteryMonitoring();
        }

        private void InitializeComponent()
        {
            this.Text = "Battify - Bluetooth Battery Monitor";
            this.Size = new System.Drawing.Size(400, 300);
            this.StartPosition = FormStartPosition.CenterScreen;
            
            // Handle form closing to minimize instead of exit
            this.FormClosing += MainForm_FormClosing;
        }

        private void CreateTrayIcon()
        {
            // Create context menu
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Show Connected Devices", null, ShowConnectedDevices);
            trayMenu.Items.Add("-");
            trayMenu.Items.Add("Settings", null, ShowSettings);
            trayMenu.Items.Add("Refresh", null, RefreshDevices);
            trayMenu.Items.Add("-");
            trayMenu.Items.Add("About", null, ShowAbout);
            trayMenu.Items.Add("Exit", null, OnExit);

            // Create tray icon
            trayIcon = new NotifyIcon()
            {
                Text = "Battify - Bluetooth Battery Monitor",
                Icon = CreateBatteryIcon(100), // Default icon
                ContextMenuStrip = trayMenu,
                Visible = true
            };

            trayIcon.DoubleClick += TrayIcon_DoubleClick;
            trayIcon.BalloonTipClicked += TrayIcon_BalloonTipClicked;
        }

        private Icon CreateBatteryIcon(int batteryLevel)
        {
            // Create a simple battery icon based on level
            Bitmap bitmap = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                
                // Draw battery outline
                g.DrawRectangle(Pens.Black, 2, 4, 10, 8);
                g.DrawRectangle(Pens.Black, 12, 6, 2, 4);
                
                // Fill battery based on level
                int fillWidth = Math.Max(1, (int)(8 * (batteryLevel / 100.0)));
                Brush fillBrush;
                if (batteryLevel > 60)
                    fillBrush = new SolidBrush(Color.FromArgb(0, 255, 0)); // Bright Green
                else if (batteryLevel > 30)
                    fillBrush = new SolidBrush(Color.FromArgb(255, 165, 0)); // Bright Orange
                else
                    fillBrush = Brushes.Red; // Keep red for low battery
                
                g.FillRectangle(fillBrush, 3, 5, fillWidth, 6);
                
                if (batteryLevel > 30)
                    fillBrush.Dispose();
                
                // Add update badge if update is available
                if (hasUpdateAvailable)
                {
                    // Draw a small red/orange circle with white border in top-right
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.FillEllipse(Brushes.White, 9, 0, 7, 7); // White border
                    g.FillEllipse(Brushes.OrangeRed, 10, 1, 5, 5); // Orange-red dot
                }
            }
            
            // Create icon and dispose bitmap
            IntPtr hIcon = bitmap.GetHicon();
            Icon icon = Icon.FromHandle(hIcon);
            bitmap.Dispose();
            
            // Clone the icon so we can safely destroy the handle
            Icon clonedIcon = (Icon)icon.Clone();
            
            // Clean up the native handle (CRITICAL for preventing GDI handle leaks)
            NativeMethods.DestroyIcon(hIcon);
            
            return clonedIcon;
        }

        private void StartBatteryMonitoring()
        {
            // Tier 1: Start DeviceWatcher for immediate device state changes
            StartDeviceWatcher();
            
            // Tier 2: Start periodic timer as safety net + cache refresh
            batteryCheckTimer = new System.Windows.Forms.Timer();
            batteryCheckTimer.Interval = appSettings.DeviceScanIntervalMinutes * 60 * 1000; // Convert minutes to milliseconds
            batteryCheckTimer.Tick += BatteryCheckTimer_Tick;
            batteryCheckTimer.Start();
            
            // Initial check
            RefreshDevices(null, EventArgs.Empty);

            // Check for updates after a short delay (don't slow down startup)
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000); // Wait 5 seconds after startup
                await CheckForUpdatesAsync(silent: true);
            });
        }

        #region DeviceWatcher (Tier 1 - Event-Driven)

        private void StartDeviceWatcher()
        {
            try
            {
                // Stop existing watcher if running
                StopDeviceWatcher();

                // Create device watcher for Bluetooth LE devices
                string bluetoothSelector = BluetoothLEDevice.GetDeviceSelector();
                string[] requestedProperties = new[] 
                { 
                    "System.Devices.Aep.DeviceAddress", 
                    "System.Devices.Aep.IsConnected",
                    "System.Devices.Aep.IsPresent"
                };

                deviceWatcher = DeviceInformation.CreateWatcher(
                    bluetoothSelector,
                    requestedProperties,
                    DeviceInformationKind.AssociationEndpoint
                );

                // Subscribe to events
                deviceWatcher.Added += DeviceWatcher_Added;
                deviceWatcher.Updated += DeviceWatcher_Updated;
                deviceWatcher.Removed += DeviceWatcher_Removed;
                deviceWatcher.EnumerationCompleted += DeviceWatcher_EnumerationCompleted;
                deviceWatcher.Stopped += DeviceWatcher_Stopped;

                // Start watching
                deviceWatcher.Start();
                Log("[WATCHER] DeviceWatcher started");
            }
            catch (Exception ex)
            {
                Log($"[WATCHER] Failed to start DeviceWatcher: {ex.Message}");
                // Fall back to timer-only mode - still functional
            }
        }

        private void StopDeviceWatcher()
        {
            if (deviceWatcher != null)
            {
                try
                {
                    if (deviceWatcher.Status == DeviceWatcherStatus.Started ||
                        deviceWatcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                    {
                        deviceWatcher.Stop();
                    }

                    deviceWatcher.Added -= DeviceWatcher_Added;
                    deviceWatcher.Updated -= DeviceWatcher_Updated;
                    deviceWatcher.Removed -= DeviceWatcher_Removed;
                    deviceWatcher.EnumerationCompleted -= DeviceWatcher_EnumerationCompleted;
                    deviceWatcher.Stopped -= DeviceWatcher_Stopped;
                }
                catch (Exception ex)
                {
                    Log($"[WATCHER] Error stopping DeviceWatcher: {ex.Message}");
                }
                
                deviceWatcher = null;
                Log("[WATCHER] DeviceWatcher stopped");
            }
        }

        private void DeviceWatcher_Added(DeviceWatcher sender, DeviceInformation deviceInfo)
        {
            Log($"[WATCHER] Device added: {deviceInfo.Name} (ID: {deviceInfo.Id})");
            OnDeviceWatcherEvent("Added", deviceInfo.Id);
        }

        private void DeviceWatcher_Updated(DeviceWatcher sender, DeviceInformationUpdate deviceInfoUpdate)
        {
            Log($"[WATCHER] Device updated: {deviceInfoUpdate.Id}");
            OnDeviceWatcherEvent("Updated", deviceInfoUpdate.Id);
        }

        private void DeviceWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate deviceInfoUpdate)
        {
            Log($"[WATCHER] Device removed: {deviceInfoUpdate.Id}");
            OnDeviceWatcherEvent("Removed", deviceInfoUpdate.Id);
        }

        private void DeviceWatcher_EnumerationCompleted(DeviceWatcher sender, object args)
        {
            Log("[WATCHER] Initial enumeration completed");
        }

        private void DeviceWatcher_Stopped(DeviceWatcher sender, object args)
        {
            Log($"[WATCHER] DeviceWatcher stopped. Status: {sender.Status}");
            
            // Restart watcher if it stopped unexpectedly (not during disposal)
            if (sender.Status == DeviceWatcherStatus.Aborted)
            {
                Log("[WATCHER] Restarting DeviceWatcher after abort...");
                // Use BeginInvoke to avoid blocking the event handler
                this.BeginInvoke(new Action(() => StartDeviceWatcher()));
            }
        }

        private void OnDeviceWatcherEvent(string eventType, string deviceId)
        {
            // Throttle: ignore events within WATCHER_THROTTLE_SECONDS of last trigger
            if ((DateTime.Now - lastWatcherTriggered).TotalSeconds < WATCHER_THROTTLE_SECONDS)
            {
                Log($"[WATCHER] Throttled {eventType} event - ignoring rapid event");
                return;
            }

            lastWatcherTriggered = DateTime.Now;

            // Trigger battery check on UI thread
            // DeviceWatcher events come from a background thread
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(async () => await CheckBluetoothDevices()));
            }
            else
            {
                _ = CheckBluetoothDevices();
            }
        }

        #endregion

        private async void BatteryCheckTimer_Tick(object? sender, EventArgs e)
        {
            // Prevent overlapping scans
            if (isCheckingDevices)
            {
                Log("[PERF] Skipping scan - previous scan still in progress");
                return;
            }
            
            Log("[TIMER] Periodic scan triggered (Tier 2 safety net)");
            await CheckBluetoothDevices();
        }

        private async void RefreshDevices(object? sender, EventArgs e)
        {
            await CheckBluetoothDevices();
        }

        private async Task CheckBluetoothDevices()
        {
            if (isCheckingDevices)
                return;
            
            isCheckingDevices = true;
            try
            {
                // Define the property key for battery percentage
                // This allows us to read the Windows Cache without connecting
                string batteryPropKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
                string[] requestedProperties = new[] { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected", "System.Devices.PrimaryCategory", batteryPropKey };

                // Get all Bluetooth LE devices
                string bluetoothSelector = BluetoothLEDevice.GetDeviceSelector();
                Log($"[DEBUG] Searching for Bluetooth LE devices with selector: {bluetoothSelector}");
                
                // Pass requested properties to get cached values
                DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(bluetoothSelector, requestedProperties);
                Log($"[DEBUG] Found {devices.Count} total Bluetooth LE device(s)");

                var currentDevices = new Dictionary<string, BluetoothLEDevice>();
                bool settingsChanged = false;

                foreach (DeviceInformation deviceInfo in devices)
                {
                    try
                    {
                        Log($"[DEBUG] Device: {deviceInfo.Name} (ID: {deviceInfo.Id})");

                        // Capture device category
                        if (deviceInfo.Properties.TryGetValue("System.Devices.PrimaryCategory", out object? categoryObj))
                        {
                            string category = categoryObj?.ToString() ?? "";
                            Log($"[DEBUG] Device: {deviceInfo.Name}, Category: '{category}' (Type: {categoryObj?.GetType().Name})");
                            
                            if (!string.IsNullOrEmpty(category))
                            {
                                if (!appSettings.DeviceCategories.ContainsKey(deviceInfo.Id) || appSettings.DeviceCategories[deviceInfo.Id] != category)
                                {
                                    appSettings.DeviceCategories[deviceInfo.Id] = category;
                                    settingsChanged = true;
                                }
                            }
                        }
                        else
                        {
                            Log($"[DEBUG] Device: {deviceInfo.Name}, Category property not found");
                        }

                        BluetoothLEDevice? device = await BluetoothLEDevice.FromIdAsync(deviceInfo.Id);
                        if (device != null)
                        {
                            Log($"[DEBUG]   - Name: {device.Name}, Status: {device.ConnectionStatus}, Appearance: {device.Appearance}");

                            // Try to get category from Appearance if PrimaryCategory failed
                            if (device.Appearance != null)
                            {
                                string appearanceCategory = GetCategoryFromAppearance(device.Appearance.RawValue);
                                if (!string.IsNullOrEmpty(appearanceCategory))
                                {
                                    Log($"[DEBUG]   - Derived category from Appearance: {appearanceCategory}");
                                    if (!appSettings.DeviceCategories.ContainsKey(deviceInfo.Id) || appSettings.DeviceCategories[deviceInfo.Id] != appearanceCategory)
                                    {
                                        appSettings.DeviceCategories[deviceInfo.Id] = appearanceCategory;
                                        settingsChanged = true;
                                    }
                                }
                            }

                            if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                            {
                                currentDevices[device.DeviceId] = device;
                                string deviceId = device.DeviceId;
                                int batteryLevel = -1;

                                // Check if we need to force an update (Slow Loop)
                                bool forceUpdate = false;
                                if (!lastForcedUpdate.ContainsKey(deviceId))
                                {
                                    forceUpdate = true; // Never updated
                                }
                                else
                                {
                                    TimeSpan timeSinceLastUpdate = DateTime.Now - lastForcedUpdate[deviceId];
                                    if (timeSinceLastUpdate.TotalMinutes >= appSettings.BatteryUpdateIntervalMinutes)
                                    {
                                        forceUpdate = true; // Time to update
                                    }
                                }

                                if (forceUpdate)
                                {
                                    Log($"[DEBUG] Forcing GATT read for {device.Name} (Last update: {(lastForcedUpdate.ContainsKey(deviceId) ? lastForcedUpdate[deviceId].ToString() : "Never")})");
                                    batteryLevel = await GetDeviceBatteryLevel(device);
                                    if (batteryLevel >= 0)
                                    {
                                        lastForcedUpdate[deviceId] = DateTime.Now;
                                    }
                                }
                                else
                                {
                                    // Try to get cached value from DeviceInformation properties (Fast Loop)
                                    if (deviceInfo.Properties.TryGetValue(batteryPropKey, out object? propVal))
                                    {
                                        // Handle different potential types for the property
                                        if (propVal is int intVal) batteryLevel = intVal;
                                        else if (propVal is byte byteVal) batteryLevel = (int)byteVal;
                                        
                                        if (batteryLevel >= 0)
                                        {
                                            Log($"[DEBUG] Using cached battery level for {device.Name}: {batteryLevel}%");
                                        }
                                    }
                                    
                                    // If cache failed, fallback to our own last known level
                                    if (batteryLevel < 0 && lastKnownBatteryLevels.ContainsKey(deviceId))
                                    {
                                        batteryLevel = lastKnownBatteryLevels[deviceId];
                                        Log($"[DEBUG] Using internal last known level for {device.Name}: {batteryLevel}%");
                                    }
                                }
                            
                                if (batteryLevel >= 0)
                                {
                                    // Update settings only if value actually changed (reduce disk I/O)
                                    int previousLevel = appSettings.LastKnownBatteryLevels.ContainsKey(deviceId) 
                                        ? appSettings.LastKnownBatteryLevels[deviceId] 
                                        : -1;
                                    
                                    if (previousLevel != batteryLevel)
                                    {
                                        appSettings.LastKnownBatteryLevels[deviceId] = batteryLevel;
                                        settingsChanged = true;
                                    }
                                    
                                    if (!string.IsNullOrEmpty(device.Name) && 
                                        (!appSettings.DeviceNames.ContainsKey(deviceId) || appSettings.DeviceNames[deviceId] != device.Name))
                                    {
                                        appSettings.DeviceNames[deviceId] = device.Name;
                                        settingsChanged = true;
                                    }
                                    
                                    // Update local dictionary
                                    lastKnownBatteryLevels[deviceId] = batteryLevel;
                                
                                    // Check for low battery notification only for monitored devices
                                    if (ShouldMonitorDevice(deviceId))
                                    {
                                        if (batteryLevel <= appSettings.BatteryThreshold)
                                        {
                                            // Check if we should show notification (based on interval)
                                            if (ShouldShowNotification(deviceId))
                                            {
                                                ShowLowBatteryNotification(device.Name ?? "Unknown Device", batteryLevel);
                                                lastNotificationTime[deviceId] = DateTime.Now;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Ignore individual device errors
                        Log($"Error checking device {deviceInfo.Name}: {ex.Message}");
                    }
                }

                // Dispose old devices that are no longer connected (prevent memory leak)
                foreach (var oldDevice in connectedDevices.Values)
                {
                    if (!currentDevices.ContainsKey(oldDevice.DeviceId))
                    {
                        oldDevice?.Dispose();
                    }
                }
                
                connectedDevices = currentDevices;
                
                if (settingsChanged)
                {
                    appSettings.Save();
                }
                
                UpdateTrayIcon();
                
                // Notify subscribers that devices have been refreshed
                DevicesRefreshed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log($"Error checking Bluetooth devices: {ex.Message}");
                if (trayIcon != null)
                {
                    trayIcon.Text = "Battify - Error accessing Bluetooth";
                }
            }
            finally
            {
                isCheckingDevices = false;
            }
        }

        private async Task<int> GetDeviceBatteryLevel(BluetoothLEDevice device)
        {
            GattDeviceService? batteryService = null;
            try
            {
                Log($"[DEBUG] Attempting to read battery level for {device.Name}");
                
                // Get GATT services for the device
                GattDeviceServicesResult servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                
                if (servicesResult.Status != GattCommunicationStatus.Success)
                {
                    Log($"[DEBUG]   - Failed to get GATT services: {servicesResult.Status}");
                    return -1;
                }
                
                Log($"[DEBUG]   - Found {servicesResult.Services.Count} GATT service(s)");
                
                // Look for the Battery Service
                foreach (var service in servicesResult.Services)
                {
                    Log($"[DEBUG]     - Service UUID: {service.Uuid}");
                    if (service.Uuid == BATTERY_SERVICE_UUID)
                    {
                        batteryService = service;
                        Log($"[DEBUG]   - Found Battery Service!");
                        break;
                    }
                }
                
                if (batteryService == null)
                {
                    Log($"[DEBUG]   - No Battery Service found for {device.Name}");
                    return -1;
                }
                
                // Get the Battery Level characteristic
                GattCharacteristicsResult characteristicsResult = await batteryService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                
                if (characteristicsResult.Status != GattCommunicationStatus.Success)
                {
                    Log($"[DEBUG]   - Failed to get characteristics: {characteristicsResult.Status}");
                    return -1;
                }
                
                Log($"[DEBUG]   - Found {characteristicsResult.Characteristics.Count} characteristic(s)");
                
                GattCharacteristic? batteryLevelChar = null;
                foreach (var characteristic in characteristicsResult.Characteristics)
                {
                    Log($"[DEBUG]     - Characteristic UUID: {characteristic.Uuid}");
                    if (characteristic.Uuid == BATTERY_LEVEL_CHARACTERISTIC_UUID)
                    {
                        batteryLevelChar = characteristic;
                        Log($"[DEBUG]   - Found Battery Level Characteristic!");
                        break;
                    }
                }
                
                if (batteryLevelChar == null)
                {
                    Log($"[DEBUG]   - No Battery Level Characteristic found");
                    return -1;
                }
                
                // Read the battery level value
                GattReadResult readResult = await batteryLevelChar.ReadValueAsync(BluetoothCacheMode.Uncached);
                
                if (readResult.Status != GattCommunicationStatus.Success)
                {
                    Log($"[DEBUG]   - Failed to read battery level: {readResult.Status}");
                    return -1;
                }
                
                // Battery level is a single byte (0-100)
                DataReader reader = DataReader.FromBuffer(readResult.Value);
                byte batteryLevel = reader.ReadByte();
                
                Log($"[DEBUG]   - Successfully read battery level: {batteryLevel}%");
                
                return batteryLevel;
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Error getting battery level for {device.Name}: {ex.Message}");
                Log($"[ERROR] Stack trace: {ex.StackTrace}");
            }
            finally
            {
                // Always dispose the GATT service to prevent resource leaks
                batteryService?.Dispose();
            }
            
            return -1; // Battery level unknown
        }

        private void UpdateTrayIcon()
        {
            if (trayIcon == null) return;

            // Filter to only show monitored devices
            var monitoredBatteryLevels = new Dictionary<string, int>();
            
            foreach (var kvp in appSettings.LastKnownBatteryLevels)
            {
                string deviceId = kvp.Key;
                int level = kvp.Value;
                
                if (ShouldMonitorDevice(deviceId) && connectedDevices.ContainsKey(deviceId))
                {
                    // Use stored name or ID
                    string name = appSettings.DeviceNames.ContainsKey(deviceId) 
                        ? appSettings.DeviceNames[deviceId] 
                        : "Unknown Device";
                    
                    monitoredBatteryLevels[name] = level;
                }
            }

            Icon newIcon;
            if (monitoredBatteryLevels.Count == 0)
            {
                trayIcon.Text = "Battify - No connected devices";
                newIcon = CreateBatteryIcon(100);
            }
            else
            {
                int lowestBattery = monitoredBatteryLevels.Values.Min();
                string deviceInfo = string.Join(", ", monitoredBatteryLevels.Select(kvp => $"{kvp.Key}: {kvp.Value}%"));
                
                // Truncate if too long for tooltip
                if (deviceInfo.Length > 60)
                {
                    deviceInfo = deviceInfo.Substring(0, 57) + "...";
                }
                
                trayIcon.Text = deviceInfo;
                newIcon = CreateBatteryIcon(lowestBattery);
            }
            
            // Dispose old icon before replacing (prevent handle leak)
            if (currentTrayIcon != null)
            {
                currentTrayIcon.Dispose();
            }
            
            trayIcon.Icon = newIcon;
            currentTrayIcon = newIcon;
        }

        private string GetDeviceIdByName(string deviceName)
        {
            var device = connectedDevices.Values.FirstOrDefault(d => d.Name == deviceName);
            return device?.DeviceId ?? string.Empty;
        }

        private bool ShouldMonitorDevice(string deviceId)
        {
            // If no devices are selected, monitor all devices
            if (appSettings.MonitoredDevices.Count == 0)
                return true;
            
            return appSettings.MonitoredDevices.Contains(deviceId);
        }

        private bool ShouldShowNotification(string deviceKey)
        {
            if (!lastNotificationTime.ContainsKey(deviceKey))
                return true;
            
            var timeSinceLastNotification = DateTime.Now - lastNotificationTime[deviceKey];
            return timeSinceLastNotification.TotalMinutes >= appSettings.NotificationIntervalMinutes;
        }

        private void ShowLowBatteryNotification(string deviceName, int batteryLevel)
        {
            if (trayIcon != null)
            {
                trayIcon.ShowBalloonTip(5000, "Low Battery Warning", 
                    $"{deviceName} battery is low: {batteryLevel}% (Threshold: {appSettings.BatteryThreshold}%)", ToolTipIcon.Warning);
            }
        }

        private string GetCategoryFromAppearance(ushort appearance)
        {
            // Log the raw appearance for debugging
            Log($"[DEBUG]     - Raw Appearance: {appearance}");

            // Bluetooth SIG Assigned Numbers for Appearance
            // https://specificationrefs.bluetooth.com/assigned-values/Appearance%20Values.pdf
            
            if (appearance == 961) return "Keyboard";
            if (appearance == 962) return "Mouse";
            if (appearance == 963 || appearance == 964) return "Game Controller";
            if (appearance == 965) return "Pen";
            if (appearance == 966) return "Card Reader";
            if (appearance == 967) return "Pen";
            if (appearance == 968) return "Barcode Scanner";
            if (appearance >= 960 && appearance <= 968) return "Input Device"; // Generic HID
            
            if (appearance == 64) return "Phone";
            if (appearance == 128) return "Computer";
            if (appearance == 192 || appearance == 193) return "Watch";
            if (appearance == 448 || appearance == 449 || appearance == 450) return "Eye Glasses";
            if (appearance == 512) return "Tag";
            if (appearance == 576 || appearance == 577) return "Keyring";
            if (appearance == 640) return "Media Player";
            if (appearance == 704) return "Barcode Scanner";
            if (appearance == 768) return "Thermometer";
            if (appearance == 832 || appearance == 833) return "Heart Rate Monitor";
            
            return "";
        }

        private string GetDeviceIcon(string category)
        {
            // Segoe MDL2 Assets glyphs
            if (string.IsNullOrEmpty(category)) 
            {
                return "\uE702"; // Generic Bluetooth
            }

            string originalCategory = category;
            category = category.ToLowerInvariant();
            
            // Log the category we are checking against (only if it's not empty, to avoid spam)
            // Log($"[DEBUG] Checking icon for category: '{originalCategory}'");

            if (category.Contains("mouse")) return "\uE962";
            if (category.Contains("keyboard")) return "\uE92E";
            if (category.Contains("headphone") || category.Contains("headset") || category.Contains("earbud") || category.Contains("audio")) return "\uE7F5";
            if (category.Contains("phone")) return "\uE8EA";
            if (category.Contains("computer") || category.Contains("laptop") || category.Contains("pc")) return "\uE7F8";
            if (category.Contains("game") || category.Contains("controller") || category.Contains("joystick")) return "\uE7FC";
            if (category.Contains("pen") || category.Contains("stylus")) return "\uED35";
            if (category.Contains("watch") || category.Contains("wearable")) return "\uE916"; 
            if (category.Contains("remote")) return "\uE88E"; // Remote Control
            // if (category.Contains("input")) return "\uE962"; // Fallback for generic HID to Mouse icon (most common) 

            return "\uE702"; // Generic Bluetooth
        }

        // Cached font for device icons to avoid repeated allocations
        private static readonly Font DeviceIconFont = new Font("Segoe MDL2 Assets", 24);

        private async void ShowConnectedDevices(object? sender, EventArgs e)
        {
            // Refresh devices - uses cache for known devices, GATT read for new ones
            await CheckBluetoothDevices();

            // Create a custom form for the connected devices dialog
            Form dialog = new Form
            {
                Text = "Connected Devices",
                Size = new Size(450, 500),
                StartPosition = FormStartPosition.CenterScreen,
                MaximizeBox = false,
                MinimizeBox = false
            };
            ModernTheme.ApplyTheme(dialog);

            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "battify_icon_full.png");
                if (File.Exists(iconPath))
                {
                    dialog.Icon = IconFromPng(iconPath);
                }
            }
            catch { }

            // Force window to front when shown
            dialog.Shown += (s, args) => {
                dialog.Activate();
                dialog.TopMost = true;
                dialog.TopMost = false;
            };

            var mainContainer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(20),
                BackColor = ModernTheme.BackgroundColor
            };

            var titleLabel = new Label
            {
                Text = "Device Status",
                Font = ModernTheme.HeaderFont,
                ForeColor = ModernTheme.TextColor,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 20)
            };
            mainContainer.Controls.Add(titleLabel);

            // Show update banner if available
            if (hasUpdateAvailable && !string.IsNullOrEmpty(availableVersion))
            {
                var updateBanner = new System.Windows.Forms.Panel
                {
                    Width = 390,
                    Height = 50,
                    BackColor = Color.FromArgb(255, 244, 229), // Light orange background
                    Margin = new Padding(0, 0, 0, 15)
                };

                var updateIcon = new Label
                {
                    Text = "\uE946", // Update icon
                    Font = new Font("Segoe MDL2 Assets", 14),
                    ForeColor = Color.FromArgb(200, 100, 0),
                    Location = new Point(12, 14),
                    AutoSize = true
                };
                updateBanner.Controls.Add(updateIcon);

                var updateText = new Label
                {
                    Text = $"Version {availableVersion} available",
                    Font = new Font(ModernTheme.BodyFont, FontStyle.Bold),
                    ForeColor = Color.FromArgb(100, 60, 0),
                    Location = new Point(45, 8),
                    AutoSize = true
                };
                updateBanner.Controls.Add(updateText);

                var downloadLink = new LinkLabel
                {
                    Text = "Download now",
                    Font = ModernTheme.BodyFont,
                    LinkColor = ModernTheme.AccentColor,
                    Location = new Point(45, 27),
                    AutoSize = true
                };
                downloadLink.LinkClicked += (s, args) =>
                {
                    if (!string.IsNullOrEmpty(latestVersionUrl))
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = latestVersionUrl,
                                UseShellExecute = true
                            });
                            ClearUpdateBadge();
                        }
                        catch { }
                    }
                };
                updateBanner.Controls.Add(downloadLink);

                mainContainer.Controls.Add(updateBanner);
            }

            // Collect all devices to show
            var devicesToShow = new List<(string Name, string DeviceId, int BatteryLevel, bool IsConnected, bool IsMonitored)>();

            // Add connected devices
            foreach (var device in connectedDevices.Values)
            {
                string deviceId = device.DeviceId;
                string name = device.Name ?? "Unknown Device";
                int batteryLevel = -1;
                if (appSettings.LastKnownBatteryLevels.ContainsKey(deviceId))
                    batteryLevel = appSettings.LastKnownBatteryLevels[deviceId];
                
                bool isMonitored = ShouldMonitorDevice(deviceId);
                devicesToShow.Add((name, deviceId, batteryLevel, true, isMonitored));
            }

            // Add disconnected monitored devices
            foreach (var kvp in appSettings.LastKnownBatteryLevels)
            {
                string deviceId = kvp.Key;
                // Only add if not already added as connected
                if (!connectedDevices.ContainsKey(deviceId) && ShouldMonitorDevice(deviceId))
                {
                    string name = appSettings.DeviceNames.ContainsKey(deviceId) ? appSettings.DeviceNames[deviceId] : "Unknown Device";
                    int batteryLevel = kvp.Value;
                    devicesToShow.Add((name, deviceId, batteryLevel, false, true));
                }
            }

            if (devicesToShow.Count == 0)
            {
                var noDevicesLabel = new Label
                {
                    Text = "No connected or monitored devices found.",
                    Font = ModernTheme.BodyFont,
                    ForeColor = ModernTheme.SecondaryTextColor,
                    AutoSize = true,
                    Margin = new Padding(5)
                };
                mainContainer.Controls.Add(noDevicesLabel);
            }
            else
            {
                foreach (var device in devicesToShow)
                {
                    var deviceCard = new ModernTheme.CardPanel
                    {
                        Width = 390,
                        Height = 90,
                        Margin = new Padding(0, 0, 0, 15)
                    };

                    // Device Icon
                    string category = appSettings.DeviceCategories.ContainsKey(device.DeviceId) ? appSettings.DeviceCategories[device.DeviceId] : "";
                    var iconLabel = new Label
                    {
                        Text = GetDeviceIcon(category),
                        Font = DeviceIconFont,
                        ForeColor = device.IsConnected ? ModernTheme.TextColor : ModernTheme.SecondaryTextColor,
                        Location = new Point(15, 25),
                        AutoSize = true
                    };
                    deviceCard.Controls.Add(iconLabel);

                    var nameLabel = new Label
                    {
                        Text = device.Name,
                        Font = ModernTheme.SubHeaderFont,
                        ForeColor = device.IsConnected ? ModernTheme.TextColor : ModernTheme.SecondaryTextColor,
                        Location = new Point(65, 15),
                        AutoSize = true
                    };
                    deviceCard.Controls.Add(nameLabel);

                    // Battery Bar
                    if (device.BatteryLevel >= 0)
                    {
                        var batteryLabel = new Label
                        {
                            Text = $"{device.BatteryLevel}%",
                            Font = ModernTheme.SubHeaderFont,
                            ForeColor = device.IsConnected ? ModernTheme.AccentColor : ModernTheme.SecondaryTextColor,
                            Location = new Point(330, 15),
                            AutoSize = true,
                            TextAlign = ContentAlignment.TopRight
                        };
                        deviceCard.Controls.Add(batteryLabel);

                        var batteryBarBg = new System.Windows.Forms.Panel
                        {
                            Location = new Point(65, 45),
                            Size = new Size(310, 6),
                            BackColor = Color.FromArgb(230, 230, 230)
                        };
                        
                        var batteryBarFill = new System.Windows.Forms.Panel
                        {
                            Height = 6,
                            Width = (int)(310 * (device.BatteryLevel / 100.0)),
                            BackColor = device.IsConnected 
                                ? (device.BatteryLevel > 20 ? ModernTheme.AccentColor : Color.Red)
                                : Color.Gray
                        };
                        batteryBarBg.Controls.Add(batteryBarFill);
                        deviceCard.Controls.Add(batteryBarBg);
                    }
                    else
                    {
                        var statusLabel = new Label
                        {
                            Text = "Battery level not available",
                            Font = ModernTheme.BodyFont,
                            ForeColor = ModernTheme.SecondaryTextColor,
                            Location = new Point(65, 45),
                            AutoSize = true
                        };
                        deviceCard.Controls.Add(statusLabel);
                    }

                    if (device.IsMonitored)
                    {
                        var monitoredLabel = new Label
                        {
                            Text = "Monitored" + (device.IsConnected ? "" : " (Disconnected)"),
                            Font = ModernTheme.SmallFont,
                            ForeColor = device.IsConnected ? Color.Green : Color.Gray,
                            Location = new Point(65, 60),
                            AutoSize = true
                        };
                        deviceCard.Controls.Add(monitoredLabel);
                    }

                    mainContainer.Controls.Add(deviceCard);
                }
            }

            var actionPanel = new System.Windows.Forms.Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                BackColor = ModernTheme.BackgroundColor,
                Padding = new Padding(0, 10, 20, 10)
            };

            var helpButton = new ModernTheme.ModernButton
            {
                Text = "Help",
                Location = new Point(20, 15),
                Size = new Size(80, 32)
            };
            helpButton.Click += (s, args) => 
            {
                MessageBox.Show(
                    "Battery monitoring requires devices with GATT Battery Service support.\n\n" +
                    "Not all Bluetooth devices report battery levels. The device must implement " +
                    "the Bluetooth GATT Battery Service (UUID: 0x180F) for battery monitoring to work.",
                    "Battery Monitoring Info",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            };
            actionPanel.Controls.Add(helpButton);

            var closeButton = new ModernTheme.PrimaryButton
            {
                Text = "Close",
                Location = new Point(330, 15),
                Size = new Size(90, 32),
                DialogResult = DialogResult.OK
            };
            actionPanel.Controls.Add(closeButton);

            dialog.Controls.Add(mainContainer);
            dialog.Controls.Add(actionPanel);
            dialog.AcceptButton = closeButton;

            dialog.ShowDialog();
        }

        private void ShowSettings(object? sender, EventArgs e)
        {
            var settingsForm = new SettingsForm(appSettings, this);
            settingsForm.SettingsSaved += OnSettingsSaved;
            settingsForm.ShowDialog();
        }

        private void OnSettingsSaved(object? sender, EventArgs e)
        {
            // Reload settings
            appSettings = Settings.Load();
            staticAppSettings = appSettings;
            
            // Update timer interval - MUST stop/start to apply new interval immediately
            if (batteryCheckTimer != null)
            {
                batteryCheckTimer.Stop();
                batteryCheckTimer.Interval = appSettings.DeviceScanIntervalMinutes * 60 * 1000;
                batteryCheckTimer.Start();
            }
            
            // Clear notification history to respect new threshold
            lastNotificationTime.Clear();
            
            // Clear forced update history to respect new BatteryUpdateIntervalMinutes
            lastForcedUpdate.Clear();
            
            // Refresh devices immediately
            RefreshDevices(null, EventArgs.Empty);
        }

        private void ShowAbout(object? sender, EventArgs e)
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string versionString = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";

            Form aboutDialog = new Form
            {
                Text = "About Battify",
                Size = new Size(350, 360),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };
            ModernTheme.ApplyTheme(aboutDialog);

            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "battify_icon_full.png");
                if (File.Exists(iconPath))
                {
                    aboutDialog.Icon = IconFromPng(iconPath);
                }
            }
            catch { }

            // Load and display icon
            PictureBox iconBox = new PictureBox
            {
                Location = new Point(100, 20),
                Size = new Size(150, 150),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent
            };

            try
            {
                // Load the high-quality icon from the Assets folder
                string pngPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "battify_icon_full.png");
                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "favicon.ico");
                
                // Prefer PNG for better quality
                if (File.Exists(pngPath))
                {
                    iconBox.Image = Image.FromFile(pngPath);
                }
                else if (File.Exists(icoPath))
                {
                    using (Icon appIcon = new Icon(icoPath, 256, 256))
                    {
                        iconBox.Image = appIcon.ToBitmap();
                    }
                }
                else
                {
                    // Fallback: try extracting from exe
                    using (Icon appIcon = new Icon(Application.ExecutablePath, 256, 256))
                    {
                        iconBox.Image = appIcon.ToBitmap();
                    }
                }
            }
            catch
            {
                // Final fallback: use a simple battery icon
                try
                {
                    iconBox.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath)?.ToBitmap();
                }
                catch
                {
                    iconBox.Image = CreateBatteryIcon(100).ToBitmap();
                }
            }

            Label nameLabel = new Label
            {
                Text = "Battify",
                Location = new Point(20, 180),
                Size = new Size(310, 30),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = ModernTheme.HeaderFont,
                ForeColor = ModernTheme.TextColor
            };

            Label versionLabel = new Label
            {
                Text = $"Version {versionString}",
                Location = new Point(20, 210),
                Size = new Size(310, 20),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = ModernTheme.BodyFont,
                ForeColor = ModernTheme.SecondaryTextColor
            };

            LinkLabel githubLink = new LinkLabel
            {
                Text = "https://github.com/sendmebits/battify",
                Location = new Point(20, 235),
                Size = new Size(310, 20),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = ModernTheme.BodyFont,
                LinkColor = ModernTheme.AccentColor,
                ActiveLinkColor = ControlPaint.Dark(ModernTheme.AccentColor),
                VisitedLinkColor = ModernTheme.AccentColor
            };
            githubLink.LinkClicked += (s, e) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = githubLink.Text,
                UseShellExecute = true
            });

            var okButton = new ModernTheme.PrimaryButton
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(125, 270),
                Size = new Size(100, 30)
            };

            aboutDialog.Controls.Add(iconBox);
            aboutDialog.Controls.Add(nameLabel);
            aboutDialog.Controls.Add(versionLabel);
            aboutDialog.Controls.Add(githubLink);
            aboutDialog.Controls.Add(okButton);
            aboutDialog.AcceptButton = okButton;

            aboutDialog.ShowDialog();
        }

        private void TrayIcon_DoubleClick(object? sender, EventArgs e)
        {
            ShowConnectedDevices(sender, e);
        }

        public static string GetLogFilePath()
        {
            return logFilePath;
        }

        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        }

        private Icon IconFromPng(string path)
        {
            using (var stream = new MemoryStream())
            {
                using (var bitmap = new Bitmap(path))
                {
                    // Write Icon Header
                    stream.Write(BitConverter.GetBytes((short)0), 0, 2); // Reserved
                    stream.Write(BitConverter.GetBytes((short)1), 0, 2); // Type (1=Icon)
                    stream.Write(BitConverter.GetBytes((short)1), 0, 2); // Count

                    // Write Icon Directory Entry
                    var width = bitmap.Width >= 256 ? 0 : bitmap.Width;
                    var height = bitmap.Height >= 256 ? 0 : bitmap.Height;
                    stream.WriteByte((byte)width);
                    stream.WriteByte((byte)height);
                    stream.WriteByte(0); // ColorCount
                    stream.WriteByte(0); // Reserved
                    stream.Write(BitConverter.GetBytes((short)1), 0, 2); // Planes
                    stream.Write(BitConverter.GetBytes((short)32), 0, 2); // BitCount
                    
                    // Write Image Data
                    using (var memoryStream = new MemoryStream())
                    {
                        bitmap.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
                        var iconImageBytes = memoryStream.ToArray();
                        
                        stream.Write(BitConverter.GetBytes(iconImageBytes.Length), 0, 4); // SizeInBytes
                        stream.Write(BitConverter.GetBytes(22), 0, 4); // ImageOffset (6 + 16)
                        
                        stream.Write(iconImageBytes, 0, iconImageBytes.Length);
                    }
                    
                    stream.Seek(0, SeekOrigin.Begin);
                    return new Icon(stream);
                }
            }
        }

        private void TrayIcon_BalloonTipClicked(object? sender, EventArgs e)
        {
            // Open the releases page when update notification is clicked
            if (!string.IsNullOrEmpty(latestVersionUrl))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = latestVersionUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Log($"[UPDATE] Failed to open URL: {ex.Message}");
                }
                latestVersionUrl = null; // Clear after opening
            }
        }

        /// <summary>
        /// Check for updates from GitHub releases
        /// </summary>
        /// <param name="silent">If true, only show notification if update available. If false, show message even if up-to-date.</param>
        /// <returns>True if an update is available</returns>
        public async Task<bool> CheckForUpdatesAsync(bool silent = true)
        {
            // Skip if update checking is disabled
            if (!appSettings.CheckForUpdates && silent)
            {
                Log("[UPDATE] Update checking is disabled");
                return false;
            }

            // Check if enough time has passed since last check (only for silent/automatic checks)
            if (silent)
            {
                var timeSinceLastCheck = DateTime.UtcNow - appSettings.LastUpdateCheckUtc;
                if (timeSinceLastCheck.TotalHours < appSettings.UpdateCheckIntervalHours)
                {
                    Log($"[UPDATE] Skipping check - last check was {timeSinceLastCheck.TotalHours:F1} hours ago");
                    return false;
                }
            }

            try
            {
                Log("[UPDATE] Checking for updates...");

                // GitHub API requires User-Agent header
                httpClient.DefaultRequestHeaders.Clear();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Battify-UpdateChecker");
                httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

                var response = await httpClient.GetStringAsync(GITHUB_RELEASES_API);
                
                // Update last check time
                appSettings.LastUpdateCheckUtc = DateTime.UtcNow;
                appSettings.Save();

                // Parse the JSON response
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                if (!root.TryGetProperty("tag_name", out var tagElement))
                {
                    Log("[UPDATE] No tag_name found in response");
                    return false;
                }

                string latestTag = tagElement.GetString() ?? "";
                string htmlUrl = root.TryGetProperty("html_url", out var urlElement) 
                    ? urlElement.GetString() ?? GITHUB_RELEASES_URL 
                    : GITHUB_RELEASES_URL;

                // Parse version from tag (remove 'v' prefix if present)
                string latestVersionStr = latestTag.TrimStart('v', 'V');
                
                // Get current version
                var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                string currentVersionStr = currentVersion != null 
                    ? $"{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}" 
                    : "0.0.0";

                Log($"[UPDATE] Current version: {currentVersionStr}, Latest version: {latestVersionStr}");

                // Compare versions
                if (Version.TryParse(latestVersionStr, out var latestVersion) &&
                    Version.TryParse(currentVersionStr, out var currentParsed))
                {
                    if (latestVersion > currentParsed)
                    {
                        // Check if user has skipped this version
                        if (silent && appSettings.SkippedVersion == latestVersionStr)
                        {
                            Log($"[UPDATE] User has skipped version {latestVersionStr}");
                            return false;
                        }

                        Log($"[UPDATE] New version available: {latestVersionStr}");
                        
                        // Store URL for balloon click handler
                        latestVersionUrl = htmlUrl;

                        // Set badge on tray icon (for both manual and automatic checks)
                        hasUpdateAvailable = true;
                        availableVersion = latestVersionStr;
                        
                        // Force immediate icon update on UI thread
                        if (this.InvokeRequired)
                        {
                            this.Invoke(new Action(() => UpdateTrayIcon())); // Use Invoke to ensure it completes before continuing
                        }
                        else
                        {
                            UpdateTrayIcon();
                            Application.DoEvents(); // Force UI refresh before MessageBox blocks
                        }

                        if (!silent)
                        {
                            // Manual check - show dialog with option to open download page
                            var result = MessageBox.Show(
                                $"A new version of Battify is available!\n\nCurrent version: {currentVersionStr}\nLatest version: {latestVersionStr}\n\nWould you like to open the download page?",
                                "Update Available",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Information);
                            
                            if (result == DialogResult.Yes)
                            {
                                try
                                {
                                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                    {
                                        FileName = htmlUrl,
                                        UseShellExecute = true
                                    });
                                }
                                catch (Exception ex)
                                {
                                    Log($"[UPDATE] Failed to open URL: {ex.Message}");
                                }
                            }
                        }
                        else
                        {
                            // Silent/automatic check - show popup notification
                            if (this.InvokeRequired)
                            {
                                this.BeginInvoke(new Action(() => ShowUpdatePopup(latestVersionStr)));
                            }
                            else
                            {
                                ShowUpdatePopup(latestVersionStr);
                            }
                        }
                        return true;
                    }
                    else
                    {
                        Log("[UPDATE] Application is up to date");
                        if (!silent)
                        {
                            MessageBox.Show(
                                $"Battify is up to date!\n\nCurrent version: {currentVersionStr}",
                                "Update Check",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                        }
                    }
                }
                else
                {
                    Log($"[UPDATE] Could not parse versions for comparison");
                }
            }
            catch (HttpRequestException ex)
            {
                Log($"[UPDATE] Network error checking for updates: {ex.Message}");
                if (!silent)
                {
                    MessageBox.Show(
                        $"Could not check for updates.\n\nPlease check your internet connection and try again.",
                        "Update Check Failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                Log($"[UPDATE] Error checking for updates: {ex.Message}");
                if (!silent)
                {
                    MessageBox.Show(
                        $"An error occurred while checking for updates:\n{ex.Message}",
                        "Update Check Failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }

            return false;
        }

        private void ShowUpdateNotification(string newVersion)
        {
            // Set badge state and store version
            hasUpdateAvailable = true;
            availableVersion = newVersion;
            
            // Update tray icon to show badge
            UpdateTrayIcon();
            
            // Show custom notification popup (more reliable than balloon tips on Windows 11)
            ShowUpdatePopup(newVersion);
        }

        private void ShowUpdatePopup(string newVersion)
        {
            // Create a custom notification form
            var popup = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Size = new Size(300, 100),
                BackColor = Color.White,
                TopMost = true,
                ShowInTaskbar = false
            };

            // Add subtle border
            popup.Paint += (s, e) =>
            {
                e.Graphics.DrawRectangle(new Pen(Color.FromArgb(200, 200, 200)), 0, 0, popup.Width - 1, popup.Height - 1);
            };

            // Icon/title area
            var titleLabel = new Label
            {
                Text = "\uE946", // Update icon from Segoe MDL2
                Font = new Font("Segoe MDL2 Assets", 16),
                ForeColor = ModernTheme.AccentColor,
                Location = new Point(10, 15),
                AutoSize = true
            };
            popup.Controls.Add(titleLabel);

            var headerLabel = new Label
            {
                Text = "Battify Update Available",
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 30, 30),
                Location = new Point(45, 12),
                AutoSize = true
            };
            popup.Controls.Add(headerLabel);

            var messageLabel = new Label
            {
                Text = $"Version {newVersion} is available to download.",
                Font = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(80, 80, 80),
                Location = new Point(45, 35),
                AutoSize = true
            };
            popup.Controls.Add(messageLabel);

            // Download button
            var downloadBtn = new Button
            {
                Text = "Download",
                FlatStyle = FlatStyle.Flat,
                BackColor = ModernTheme.AccentColor,
                ForeColor = Color.White,
                Size = new Size(80, 28),
                Location = new Point(120, 62),
                Cursor = Cursors.Hand
            };
            downloadBtn.FlatAppearance.BorderSize = 0;
            downloadBtn.Click += (s, e) =>
            {
                popup.Close();
                ClearUpdateBadge();
                if (!string.IsNullOrEmpty(latestVersionUrl))
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = latestVersionUrl,
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
            };
            popup.Controls.Add(downloadBtn);

            // Dismiss button
            var dismissBtn = new Button
            {
                Text = "Later",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(240, 240, 240),
                ForeColor = Color.FromArgb(60, 60, 60),
                Size = new Size(60, 28),
                Location = new Point(210, 62),
                Cursor = Cursors.Hand
            };
            dismissBtn.FlatAppearance.BorderSize = 0;
            dismissBtn.Click += (s, e) => popup.Close();
            popup.Controls.Add(dismissBtn);

            // Position near system tray (bottom-right of screen)
            var workingArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.GetBounds(Point.Empty);
            popup.Location = new Point(
                workingArea.Right - popup.Width - 10,
                workingArea.Bottom - popup.Height - 10
            );

            // Auto-close after 15 seconds
            var autoCloseTimer = new System.Windows.Forms.Timer { Interval = 15000 };
            autoCloseTimer.Tick += (s, e) =>
            {
                autoCloseTimer.Stop();
                if (!popup.IsDisposed)
                    popup.Close();
            };
            autoCloseTimer.Start();

            popup.FormClosed += (s, e) => autoCloseTimer.Dispose();
            popup.Show();
        }

        private void ClearUpdateBadge()
        {
            hasUpdateAvailable = false;
            availableVersion = null;
            UpdateTrayIcon();
        }

        private void OnExit(object? sender, EventArgs e)
        {
            // Stop DeviceWatcher first
            StopDeviceWatcher();
            
            currentTrayIcon?.Dispose();
            trayIcon?.Dispose();
            batteryCheckTimer?.Dispose();
            
            // Dispose connected devices
            foreach (var device in connectedDevices.Values)
            {
                device?.Dispose();
            }
            
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Stop DeviceWatcher
                StopDeviceWatcher();
                
                currentTrayIcon?.Dispose();
                trayIcon?.Dispose();
                batteryCheckTimer?.Dispose();
                trayMenu?.Dispose();
                
                // Dispose connected devices
                foreach (var device in connectedDevices.Values)
                {
                    device?.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}