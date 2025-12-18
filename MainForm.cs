using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
            batteryCheckTimer = new System.Windows.Forms.Timer();
            batteryCheckTimer.Interval = appSettings.DeviceScanIntervalSeconds * 1000; // Convert seconds to milliseconds
            batteryCheckTimer.Tick += BatteryCheckTimer_Tick;
            batteryCheckTimer.Start();
            
            // Initial check
            RefreshDevices(null, EventArgs.Empty);
        }

        private async void BatteryCheckTimer_Tick(object? sender, EventArgs e)
        {
            // Prevent overlapping scans
            if (isCheckingDevices)
            {
                Log("[PERF] Skipping scan - previous scan still in progress");
                return;
            }
            
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
                                    // Update settings
                                    appSettings.LastKnownBatteryLevels[deviceId] = batteryLevel;
                                    if (!string.IsNullOrEmpty(device.Name))
                                    {
                                        appSettings.DeviceNames[deviceId] = device.Name;
                                    }
                                    settingsChanged = true;
                                    
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
                GattDeviceService? batteryService = null;
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
                    batteryService.Dispose();
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
                    batteryService.Dispose();
                    return -1;
                }
                
                // Read the battery level value
                GattReadResult readResult = await batteryLevelChar.ReadValueAsync(BluetoothCacheMode.Uncached);
                
                if (readResult.Status != GattCommunicationStatus.Success)
                {
                    Log($"[DEBUG]   - Failed to read battery level: {readResult.Status}");
                    batteryService.Dispose();
                    return -1;
                }
                
                // Battery level is a single byte (0-100)
                DataReader reader = DataReader.FromBuffer(readResult.Value);
                byte batteryLevel = reader.ReadByte();
                
                Log($"[DEBUG]   - Successfully read battery level: {batteryLevel}%");
                
                batteryService.Dispose();
                return batteryLevel;
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Error getting battery level for {device.Name}: {ex.Message}");
                Log($"[ERROR] Stack trace: {ex.StackTrace}");
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
                        Font = new Font("Segoe MDL2 Assets", 24),
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
            
            // Update timer interval
            if (batteryCheckTimer != null)
            {
                batteryCheckTimer.Interval = appSettings.CheckIntervalMinutes * 60 * 1000;
            }
            
            // Clear notification history to respect new threshold
            lastNotificationTime.Clear();
            
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

        private void OnExit(object? sender, EventArgs e)
        {
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