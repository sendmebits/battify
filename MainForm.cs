using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace Battify
{
    public partial class MainForm : Form
    {
        private NotifyIcon? trayIcon;
        private ContextMenuStrip? trayMenu;
        private System.Windows.Forms.Timer? batteryCheckTimer;
        private Dictionary<string, BluetoothLEDevice> connectedDevices;
        private Dictionary<string, int> lastKnownBatteryLevels;
        private Dictionary<string, DateTime> lastNotificationTime;
        private Settings appSettings;

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
            lastKnownBatteryLevels = new Dictionary<string, int>();
            lastNotificationTime = new Dictionary<string, DateTime>();
            appSettings = Settings.Load();
            staticAppSettings = appSettings;
            
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
            
            return Icon.FromHandle(bitmap.GetHicon());
        }

        private void StartBatteryMonitoring()
        {
            batteryCheckTimer = new System.Windows.Forms.Timer();
            batteryCheckTimer.Interval = appSettings.CheckIntervalMinutes * 60 * 1000; // Convert minutes to milliseconds
            batteryCheckTimer.Tick += BatteryCheckTimer_Tick;
            batteryCheckTimer.Start();
            
            // Initial check
            RefreshDevices(null, EventArgs.Empty);
        }

        private async void BatteryCheckTimer_Tick(object? sender, EventArgs e)
        {
            await CheckBluetoothDevices();
        }

        private async void RefreshDevices(object? sender, EventArgs e)
        {
            await CheckBluetoothDevices();
        }

        private async Task CheckBluetoothDevices()
        {
            try
            {
                // Get all Bluetooth LE devices
                string bluetoothSelector = BluetoothLEDevice.GetDeviceSelector();
                Log($"[DEBUG] Searching for Bluetooth LE devices with selector: {bluetoothSelector}");
                DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(bluetoothSelector);
                Log($"[DEBUG] Found {devices.Count} total Bluetooth LE device(s)");

                var currentDevices = new Dictionary<string, BluetoothLEDevice>();
                var batteryLevels = new Dictionary<string, int>();

                foreach (DeviceInformation deviceInfo in devices)
                {
                    try
                    {
                        Log($"[DEBUG] Device: {deviceInfo.Name} (ID: {deviceInfo.Id})");
                        BluetoothLEDevice? device = await BluetoothLEDevice.FromIdAsync(deviceInfo.Id);
                        if (device != null)
                        {
                            Log($"[DEBUG]   - Name: {device.Name}, Status: {device.ConnectionStatus}");
                            if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                            {
                                currentDevices[device.DeviceId] = device;
                            
                                // Try to get battery level
                                int batteryLevel = await GetDeviceBatteryLevel(device);
                                string deviceKey = device.Name ?? device.DeviceId;
                            
                                if (batteryLevel >= 0)
                                {
                                    batteryLevels[deviceKey] = batteryLevel;
                                
                                    // Check for low battery notification only for monitored devices
                                    if (ShouldMonitorDevice(device.DeviceId))
                                    {
                                        if (batteryLevel <= appSettings.BatteryThreshold)
                                        {
                                            // Check if we should show notification (based on interval)
                                            if (ShouldShowNotification(deviceKey))
                                            {
                                                ShowLowBatteryNotification(device.Name ?? "Unknown Device", batteryLevel);
                                                lastNotificationTime[deviceKey] = DateTime.Now;
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
                
                // Update battery levels only if we got new data, otherwise keep the old data
                if (batteryLevels.Count > 0)
                {
                    foreach (var kvp in batteryLevels)
                    {
                        lastKnownBatteryLevels[kvp.Key] = kvp.Value;
                    }
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
            var monitoredBatteryLevels = lastKnownBatteryLevels
                .Where(kvp => ShouldMonitorDevice(GetDeviceIdByName(kvp.Key)))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            if (connectedDevices.Count == 0 || monitoredBatteryLevels.Count == 0)
            {
                trayIcon.Text = "Battify - No monitored devices";
                trayIcon.Icon = CreateBatteryIcon(100);
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
                trayIcon.Icon = CreateBatteryIcon(lowestBattery);
            }
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

        private void ShowConnectedDevices(object? sender, EventArgs e)
        {
            if (connectedDevices.Count == 0)
            {
                MessageBox.Show("No connected Bluetooth devices found.", "Connected Devices", 
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                // Create a custom form for the connected devices dialog
                Form dialog = new Form
                {
                    Text = "Connected Devices",
                    Size = new Size(400, 250),
                    StartPosition = FormStartPosition.CenterScreen,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false
                };

                Label label = new Label
                {
                    AutoSize = false,
                    Location = new Point(20, 20),
                    Size = new Size(340, 140),
                    Font = new Font("Segoe UI", 9F)
                };

                string message = "Connected Bluetooth Devices:\n\n";
                foreach (var device in connectedDevices.Values)
                {
                    string deviceName = device.Name ?? "Unknown Device";
                    string batteryInfo = lastKnownBatteryLevels.ContainsKey(deviceName) 
                        ? $" (Battery: {lastKnownBatteryLevels[deviceName]}%)"
                        : " (Battery: Not available)";
                    bool isMonitored = ShouldMonitorDevice(device.DeviceId);
                    string monitoringStatus = isMonitored ? " [Monitored]" : "";
                    message += $"• {deviceName}{batteryInfo}{monitoringStatus}\n";
                }
                label.Text = message;

                Button helpButton = new Button
                {
                    Text = "?",
                    Location = new Point(20, 170),
                    Size = new Size(30, 30),
                    Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                    FlatStyle = FlatStyle.System
                };
                helpButton.Click += (s, args) => 
                {
                    MessageBox.Show(
                        "Battery monitoring requires devices with GATT Battery Service support.\n\n" +
                        "Not all Bluetooth devices report battery levels. The device must implement " +
                        "the Bluetooth GATT Battery Service (UUID: 0x180F) for battery monitoring to work.",
                        "Battery Monitoring Info",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.None);
                };

                Button okButton = new Button
                {
                    Text = "OK",
                    DialogResult = DialogResult.OK,
                    Location = new Point(285, 170),
                    Size = new Size(75, 30)
                };

                dialog.Controls.Add(label);
                dialog.Controls.Add(helpButton);
                dialog.Controls.Add(okButton);
                dialog.AcceptButton = okButton;

                dialog.ShowDialog();
            }
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
                Size = new Size(350, 330),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            // Load and display icon
            PictureBox iconBox = new PictureBox
            {
                Location = new Point(100, 20),
                Size = new Size(150, 150),
                SizeMode = PictureBoxSizeMode.Zoom
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
                Font = new Font("Segoe UI", 16F, FontStyle.Bold)
            };

            Label versionLabel = new Label
            {
                Text = $"Version {versionString}",
                Location = new Point(20, 210),
                Size = new Size(310, 20),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9F)
            };

            Button okButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(125, 245),
                Size = new Size(100, 30)
            };

            aboutDialog.Controls.Add(iconBox);
            aboutDialog.Controls.Add(nameLabel);
            aboutDialog.Controls.Add(versionLabel);
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

        private void OnExit(object? sender, EventArgs e)
        {
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