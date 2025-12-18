using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Windows.Devices.Bluetooth;
using Microsoft.Win32;

namespace Battify
{
    public class SettingsForm : Form
    {
        private Settings settings;
        private MainForm mainForm;

        private FlowLayoutPanel? devicesListPanel;
        private NumericUpDown? thresholdNumeric;
        private NumericUpDown? intervalNumeric;
        private NumericUpDown? checkIntervalNumeric; // Maps to BatteryUpdateIntervalMinutes (Slow loop)
        private NumericUpDown? deviceScanIntervalNumeric; // Maps to DeviceScanIntervalSeconds (Fast loop)
        private CheckBox? loggingCheckBox;
        private CheckBox? startWithWindowsCheckBox;
        private Button? viewLogButton;
        private Button? saveButton;
        private Button? cancelButton;
        private Button? refreshButton;
        private Label? statusLabel;
        private System.Windows.Forms.Timer? statusTimer;
        private System.Windows.Forms.Timer? closeTimer;

        public event EventHandler? SettingsSaved;

        public SettingsForm(Settings settings, MainForm mainForm)
        {
            this.settings = settings;
            this.mainForm = mainForm;
            
            // Subscribe to the DevicesRefreshed event
            mainForm.DevicesRefreshed += OnDevicesRefreshed;
            this.FormClosed += (s, e) => mainForm.DevicesRefreshed -= OnDevicesRefreshed;
            
            InitializeComponent();
            SetIcon();
            LoadSettings();
        }

        private void OnDevicesRefreshed(object? sender, EventArgs e)
        {
            // Refresh the device list UI on the UI thread
            if (InvokeRequired)
            {
                Invoke(new Action(() => 
                {
                    RefreshDeviceList();
                    ShowStatusMessage("Device list refreshed!", Color.Green);
                }));
            }
            else
            {
                RefreshDeviceList();
                ShowStatusMessage("Device list refreshed!", Color.Green);
            }
        }

        private void ShowStatusMessage(string message, Color color)
        {
            if (statusLabel != null)
            {
                statusLabel.Text = message;
                statusLabel.ForeColor = color;
                statusLabel.Visible = true;
                
                // Reset timer - dispose old one first
                statusTimer?.Stop();
                statusTimer?.Dispose();
                statusTimer = new System.Windows.Forms.Timer();
                statusTimer.Interval = 3000; // 3 seconds
                statusTimer.Tick += (s, e) =>
                {
                    statusLabel.Text = "";
                    statusLabel.Visible = false;
                    statusTimer?.Stop();
                };
                statusTimer.Start();
            }
        }

        private void SetIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "battify_icon_full.png");
                if (File.Exists(iconPath))
                {
                    this.Icon = IconFromPng(iconPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load icon: {ex.Message}");
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

        private void InitializeComponent()
        {
            ModernTheme.ApplyTheme(this);
            this.Text = "Battify Settings";
            this.Size = new Size(450, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            // Main container with scrolling
            var mainContainer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(20),
                BackColor = ModernTheme.BackgroundColor
            };

            // Title
            var titleLabel = new Label
            {
                Text = "Settings",
                Font = ModernTheme.HeaderFont,
                ForeColor = ModernTheme.TextColor,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 20)
            };
            mainContainer.Controls.Add(titleLabel);

            // --- Devices Card ---
            var devicesCard = new ModernTheme.CardPanel { Width = 390, Height = 180 };
            
            var devicesHeader = new Label
            {
                Text = "Monitored Devices",
                Font = ModernTheme.SubHeaderFont,
                ForeColor = ModernTheme.TextColor,
                AutoSize = true,
                Location = new Point(15, 15)
            };
            devicesCard.Controls.Add(devicesHeader);

            devicesListPanel = new FlowLayoutPanel
            {
                Location = new Point(15, 45),
                Size = new Size(360, 80),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = ModernTheme.SurfaceColor
            };
            devicesCard.Controls.Add(devicesListPanel);

            refreshButton = new ModernTheme.ModernButton
            {
                Text = "Refresh List",
                Location = new Point(15, 135),
                Size = new Size(100, 30)
            };
            refreshButton.Click += RefreshButton_Click;
            devicesCard.Controls.Add(refreshButton);

            statusLabel = new Label
            {
                Text = "",
                Location = new Point(125, 140),
                Size = new Size(250, 20),
                ForeColor = Color.Green,
                Font = new Font(ModernTheme.BodyFont, FontStyle.Italic),
                Visible = false
            };
            devicesCard.Controls.Add(statusLabel);

            mainContainer.Controls.Add(devicesCard);

            // --- Notifications Card ---
            var notifyCard = new ModernTheme.CardPanel { Width = 390, Height = 210 };
            
            var notifyHeader = new Label
            {
                Text = "Configuration",
                Font = ModernTheme.SubHeaderFont,
                ForeColor = ModernTheme.TextColor,
                AutoSize = true,
                Location = new Point(15, 15)
            };
            notifyCard.Controls.Add(notifyHeader);

            // Threshold
            var thresholdLabel = new Label
            {
                Text = "Low Battery Threshold:",
                Location = new Point(15, 50),
                AutoSize = true,
                Font = ModernTheme.BodyFont
            };
            notifyCard.Controls.Add(thresholdLabel);

            thresholdNumeric = new NumericUpDown
            {
                Location = new Point(210, 48),
                Size = new Size(70, 25),
                Minimum = 1,
                Maximum = 100,
                Value = 25,
                BorderStyle = BorderStyle.FixedSingle
            };
            notifyCard.Controls.Add(thresholdNumeric);
            
            var thresholdUnit = new Label { Text = "%", Location = new Point(285, 50), AutoSize = true };
            notifyCard.Controls.Add(thresholdUnit);

            var thresholdHelp = new ModernTheme.ModernButton
            {
                Text = "?",
                Location = new Point(350, 48),
                Size = new Size(25, 25)
            };
            thresholdHelp.Click += (s, e) => MessageBox.Show(
                "This setting determines when you'll receive low battery notifications.\n\n" +
                "When a device's battery level drops below this percentage, " +
                "Battify will show a notification to alert you.",
                "Low Battery Threshold", MessageBoxButtons.OK, MessageBoxIcon.Information);
            notifyCard.Controls.Add(thresholdHelp);

            // Interval
            var intervalLabel = new Label
            {
                Text = "Remind every:",
                Location = new Point(15, 90),
                AutoSize = true,
                Font = ModernTheme.BodyFont
            };
            notifyCard.Controls.Add(intervalLabel);

            intervalNumeric = new NumericUpDown
            {
                Location = new Point(210, 88),
                Size = new Size(70, 25),
                Minimum = 1,
                Maximum = 1440,
                Value = 60,
                BorderStyle = BorderStyle.FixedSingle
            };
            notifyCard.Controls.Add(intervalNumeric);

            var intervalUnit = new Label { Text = "minutes", Location = new Point(285, 90), AutoSize = true };
            notifyCard.Controls.Add(intervalUnit);

            var intervalHelp = new ModernTheme.ModernButton
            {
                Text = "?",
                Location = new Point(350, 88),
                Size = new Size(25, 25)
            };
            intervalHelp.Click += (s, e) => MessageBox.Show(
                "This controls how often you'll be reminded about low battery devices.\n\n" +
                "If a device remains below the battery threshold, Battify will show " +
                "another notification after this many minutes have passed since the last notification.",
                "Notification Repeat Interval", MessageBoxButtons.OK, MessageBoxIcon.Information);
            notifyCard.Controls.Add(intervalHelp);

            // Device Scan Interval (Fast Loop)
            var scanLabel = new Label
            {
                Text = "Scan devices every:",
                Location = new Point(15, 130),
                AutoSize = true,
                Font = ModernTheme.BodyFont
            };
            notifyCard.Controls.Add(scanLabel);

            deviceScanIntervalNumeric = new NumericUpDown
            {
                Location = new Point(210, 128),
                Size = new Size(70, 25),
                Minimum = 10,
                Maximum = 300,
                Value = 60,
                BorderStyle = BorderStyle.FixedSingle
            };
            notifyCard.Controls.Add(deviceScanIntervalNumeric);

            var scanUnit = new Label { Text = "seconds", Location = new Point(285, 130), AutoSize = true };
            notifyCard.Controls.Add(scanUnit);

            var scanHelp = new ModernTheme.ModernButton
            {
                Text = "?",
                Location = new Point(350, 128),
                Size = new Size(25, 25)
            };
            scanHelp.Click += (s, e) => MessageBox.Show(
                "This controls how often Battify checks for connected devices and reads their CACHED battery levels.\n\n" +
                "This is a 'cheap' operation and can be done frequently (e.g., every 60 seconds) without draining device battery.",
                "Device Scan Interval", MessageBoxButtons.OK, MessageBoxIcon.Information);
            notifyCard.Controls.Add(scanHelp);

            // Force Update Interval (Slow Loop)
            var checkLabel = new Label
            {
                Text = "Force update every:",
                Location = new Point(15, 170),
                AutoSize = true,
                Font = ModernTheme.BodyFont
            };
            notifyCard.Controls.Add(checkLabel);

            checkIntervalNumeric = new NumericUpDown
            {
                Location = new Point(210, 168),
                Size = new Size(70, 25),
                Minimum = 1,
                Maximum = 1440,
                Value = 90,
                BorderStyle = BorderStyle.FixedSingle
            };
            notifyCard.Controls.Add(checkIntervalNumeric);

            var checkUnit = new Label { Text = "minutes", Location = new Point(285, 170), AutoSize = true };
            notifyCard.Controls.Add(checkUnit);

            var checkHelp = new ModernTheme.ModernButton
            {
                Text = "?",
                Location = new Point(350, 168),
                Size = new Size(25, 25)
            };
            checkHelp.Click += (s, e) => MessageBox.Show(
                "This controls how often Battify forces a fresh battery reading from the device.\n\n" +
                "WARNING: This wakes up the device radio and consumes battery power. Keep this interval long (e.g., 30+ minutes) to preserve battery life.",
                "Force Update Interval", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            notifyCard.Controls.Add(checkHelp);

            mainContainer.Controls.Add(notifyCard);

            // --- System Card ---
            var systemCard = new ModernTheme.CardPanel { Width = 390, Height = 120 };
            
            var systemHeader = new Label
            {
                Text = "System",
                Font = ModernTheme.SubHeaderFont,
                ForeColor = ModernTheme.TextColor,
                AutoSize = true,
                Location = new Point(15, 15)
            };
            systemCard.Controls.Add(systemHeader);

            startWithWindowsCheckBox = new CheckBox
            {
                Text = "Start automatically with Windows",
                Location = new Point(15, 45),
                AutoSize = true,
                Font = ModernTheme.BodyFont,
                FlatStyle = FlatStyle.Flat
            };
            systemCard.Controls.Add(startWithWindowsCheckBox);

            loggingCheckBox = new CheckBox
            {
                Text = "Enable Debug Logging",
                Location = new Point(15, 75),
                AutoSize = true,
                Font = ModernTheme.BodyFont,
                FlatStyle = FlatStyle.Flat
            };
            loggingCheckBox.CheckedChanged += LoggingCheckBox_CheckedChanged;
            systemCard.Controls.Add(loggingCheckBox);

            viewLogButton = new ModernTheme.ModernButton
            {
                Text = "View Log",
                Location = new Point(250, 70),
                Size = new Size(80, 25),
                Visible = false
            };
            viewLogButton.Click += ViewLogButton_Click;
            systemCard.Controls.Add(viewLogButton);

            mainContainer.Controls.Add(systemCard);

            // --- Action Buttons ---
            var actionPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                BackColor = ModernTheme.BackgroundColor,
                Padding = new Padding(0, 10, 20, 10)
            };

            saveButton = new ModernTheme.PrimaryButton
            {
                Text = "Save",
                Location = new Point(230, 15),
                Size = new Size(90, 32),
                DialogResult = DialogResult.OK
            };
            saveButton.Click += SaveButton_Click;
            actionPanel.Controls.Add(saveButton);

            cancelButton = new ModernTheme.ModernButton
            {
                Text = "Cancel",
                Location = new Point(330, 15),
                Size = new Size(90, 32),
                DialogResult = DialogResult.Cancel
            };
            actionPanel.Controls.Add(cancelButton);

            this.Controls.Add(mainContainer);
            this.Controls.Add(actionPanel);
            
            this.AcceptButton = saveButton;
            this.CancelButton = cancelButton;
        }

        private void LoadSettings()
        {
            // Populate device list (this will also restore checked state)
            RefreshDeviceList();

            // Load settings values
            if (thresholdNumeric != null)
                thresholdNumeric.Value = settings.BatteryThreshold;
            if (intervalNumeric != null)
                intervalNumeric.Value = settings.NotificationIntervalMinutes;
            if (checkIntervalNumeric != null)
                checkIntervalNumeric.Value = settings.BatteryUpdateIntervalMinutes;
            if (deviceScanIntervalNumeric != null)
                deviceScanIntervalNumeric.Value = settings.DeviceScanIntervalSeconds;

            // Load logging settings
            if (loggingCheckBox != null)
            {
                loggingCheckBox.Checked = settings.LoggingEnabled;
                UpdateViewLogButtonVisibility();
            }

            // Load start with Windows setting
            if (startWithWindowsCheckBox != null)
            {
                // Verify registry state matches saved setting
                bool registryEnabled = IsStartupWithWindowsEnabled();
                if (registryEnabled != settings.StartWithWindows)
                {
                    // Sync the setting with actual registry state
                    settings.StartWithWindows = registryEnabled;
                }
                startWithWindowsCheckBox.Checked = settings.StartWithWindows;
            }
        }

        private void RefreshDeviceList()
        {
            if (devicesListPanel == null) return;
            
            // Get current device data from MainForm
            var availableDevices = mainForm.ConnectedDevices;
            var currentBatteryLevels = mainForm.LastKnownBatteryLevels;
            
            // Capture currently monitored devices from settings and current UI state
            var monitoredDeviceIds = new HashSet<string>(settings.MonitoredDevices);
            
            foreach (Control control in devicesListPanel.Controls)
            {
                if (control is Panel row && row.Tag is string deviceId)
                {
                    var toggle = row.Controls.OfType<ModernTheme.ToggleSwitch>().FirstOrDefault();
                    if (toggle != null)
                    {
                        if (toggle.IsOn) monitoredDeviceIds.Add(deviceId);
                        else monitoredDeviceIds.Remove(deviceId);
                    }
                }
            }
            
            devicesListPanel.Controls.Clear();
            devicesListPanel.SuspendLayout();
            
            var addedDeviceIds = new HashSet<string>();
            
            // Helper to add a row
            void AddDeviceRow(string deviceId, string displayName, bool isConnected, bool isMonitored)
            {
                var row = new Panel
                {
                    Width = devicesListPanel.Width - 25, // Account for scrollbar
                    Height = 30,
                    Tag = deviceId,
                    Margin = new Padding(0, 0, 0, 5)
                };

                var label = new Label
                {
                    Text = displayName,
                    AutoSize = true,
                    Location = new Point(0, 5),
                    ForeColor = isConnected ? ModernTheme.TextColor : ModernTheme.SecondaryTextColor
                };
                
                var toggle = new ModernTheme.ToggleSwitch
                {
                    IsOn = isMonitored,
                    Location = new Point(row.Width - 45, 5),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right
                };

                row.Controls.Add(label);
                row.Controls.Add(toggle);
                devicesListPanel.Controls.Add(row);
            }
            
            // First, add all currently connected devices
            if (availableDevices.Count > 0)
            {
                // Sort devices alphabetically by name
                var sortedDevices = availableDevices.Values
                    .OrderBy(d => d.Name ?? "Unknown Device")
                    .ToList();

                foreach (var device in sortedDevices)
                {
                    string deviceName = device.Name ?? "Unknown Device";
                    // Use DeviceId for lookup
                    string batteryInfo = currentBatteryLevels.ContainsKey(device.DeviceId)
                        ? $" [{currentBatteryLevels[device.DeviceId]}%]"
                        : " [Battery unknown]";

                    // Update the device name in settings for persistence
                    if (!string.IsNullOrEmpty(device.Name))
                    {
                        settings.DeviceNames[device.DeviceId] = device.Name;
                    }

                    bool isMonitored = monitoredDeviceIds.Contains(device.DeviceId);
                    
                    AddDeviceRow(device.DeviceId, $"{deviceName}{batteryInfo}", true, isMonitored);
                    addedDeviceIds.Add(device.DeviceId);
                }
            }
            
            // Combine monitored devices with all known devices (from history)
            var allKnownDevices = new HashSet<string>(monitoredDeviceIds);
            foreach (var id in settings.DeviceNames.Keys) allKnownDevices.Add(id);
            foreach (var id in settings.LastKnownBatteryLevels.Keys) allKnownDevices.Add(id);

            // Now add any known devices that are not currently connected (greyed out)
            foreach (var deviceId in allKnownDevices)
            {
                if (!addedDeviceIds.Contains(deviceId))
                {
                    // This is a known device that's currently disconnected
                    // Use the persisted name if available, otherwise try to extract from ID
                    string deviceName = settings.DeviceNames.ContainsKey(deviceId)
                        ? settings.DeviceNames[deviceId]
                        : ExtractDeviceNameFromId(deviceId);
                    
                    // Check for last known battery level
                    string batteryInfo = "";
                    if (settings.LastKnownBatteryLevels.ContainsKey(deviceId))
                    {
                        batteryInfo = $" [{settings.LastKnownBatteryLevels[deviceId]}%]";
                    }
                    else if (currentBatteryLevels.ContainsKey(deviceId))
                    {
                        batteryInfo = $" [{currentBatteryLevels[deviceId]}%]";
                    }
                    
                    bool isMonitored = monitoredDeviceIds.Contains(deviceId);
                    AddDeviceRow(deviceId, $"{deviceName}{batteryInfo} [Disconnected]", false, isMonitored);
                }
            }
            
            // Show a message if there are no devices at all
            if (devicesListPanel.Controls.Count == 0)
            {
                var label = new Label
                {
                    Text = "No Bluetooth devices found",
                    AutoSize = false,
                    Width = devicesListPanel.Width - 10,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = ModernTheme.SecondaryTextColor
                };
                devicesListPanel.Controls.Add(label);
            }
            
            devicesListPanel.ResumeLayout();
        }
        
        private string ExtractDeviceNameFromId(string deviceId)
        {
            // Device IDs in Windows often contain the device name
            // Try to extract it, otherwise return a generic name
            try
            {
                // Windows device IDs often have format like:
                // BluetoothLE#BluetoothLE12:34:56:78:9a:bc-d4:12:65:6b:18:da#...
                // Or they might include the device name
                if (deviceId.Contains("#"))
                {
                    var parts = deviceId.Split('#');
                    if (parts.Length > 1)
                    {
                        // Try to find a human-readable part
                        foreach (var part in parts)
                        {
                            if (!part.StartsWith("Bluetooth") && 
                                !part.Contains("{") && 
                                !part.Contains("-") &&
                                part.Length > 0 && 
                                part.Length < 50)
                            {
                                return part;
                            }
                        }
                    }
                }
            }
            catch { }
            
            return "Unknown Device";
        }
        
        private void RefreshButton_Click(object? sender, EventArgs e)
        {
            ShowStatusMessage("Refreshing devices...", Color.Blue);
            
            // Trigger a refresh on the main form
            // The OnDevicesRefreshed event handler will update the UI when complete
            mainForm.Invoke(new Action(() => 
            {
                // Call the public RefreshDevices method via reflection or make it public
                // For now, we'll use a workaround by triggering the tray menu refresh
                var refreshMethod = mainForm.GetType().GetMethod("RefreshDevices", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                refreshMethod?.Invoke(mainForm, new object?[] { null, EventArgs.Empty });
            }));
        }

        private void SaveButton_Click(object? sender, EventArgs e)
        {
            // Save settings
            if (thresholdNumeric != null)
                settings.BatteryThreshold = (int)thresholdNumeric.Value;
            if (intervalNumeric != null)
                settings.NotificationIntervalMinutes = (int)intervalNumeric.Value;
            if (checkIntervalNumeric != null)
                settings.BatteryUpdateIntervalMinutes = (int)checkIntervalNumeric.Value;
            if (deviceScanIntervalNumeric != null)
                settings.DeviceScanIntervalSeconds = (int)deviceScanIntervalNumeric.Value;

            // Save logging settings
            if (loggingCheckBox != null)
                settings.LoggingEnabled = loggingCheckBox.Checked;

            // Save start with Windows setting
            if (startWithWindowsCheckBox != null)
            {
                settings.StartWithWindows = startWithWindowsCheckBox.Checked;
                try
                {
                    if (startWithWindowsCheckBox.Checked)
                    {
                        SetStartupWithWindows(true);
                    }
                    else
                    {
                        SetStartupWithWindows(false);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Failed to update Windows startup setting:\n{ex.Message}",
                        "Startup Setting Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }

            // Save monitored devices
            settings.MonitoredDevices.Clear();
            var newMonitoredDeviceIds = new HashSet<string>();
            
            if (devicesListPanel != null)
            {
                foreach (Control control in devicesListPanel.Controls)
                {
                    if (control is Panel row && row.Tag is string deviceId)
                    {
                        var toggle = row.Controls.OfType<ModernTheme.ToggleSwitch>().FirstOrDefault();
                        if (toggle != null && toggle.IsOn)
                        {
                            settings.MonitoredDevices.Add(deviceId);
                            newMonitoredDeviceIds.Add(deviceId);
                        }
                    }
                }
            }

            // Clean up device names for devices that are no longer monitored
            var deviceIdsToRemove = settings.DeviceNames.Keys
                .Where(deviceId => !newMonitoredDeviceIds.Contains(deviceId))
                .ToList();
            
            foreach (var deviceId in deviceIdsToRemove)
            {
                settings.DeviceNames.Remove(deviceId);
            }

            settings.Save();

            if (statusLabel != null)
            {
                statusLabel.Text = "Settings saved successfully!";
                statusLabel.ForeColor = Color.Green;
            }

            SettingsSaved?.Invoke(this, EventArgs.Empty);

            // Close after a brief delay
            closeTimer?.Dispose();
            closeTimer = new System.Windows.Forms.Timer();
            closeTimer.Interval = 1000;
            closeTimer.Tick += (s, args) =>
            {
                closeTimer?.Stop();
                closeTimer?.Dispose();
                this.Close();
            };
            closeTimer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                statusTimer?.Dispose();
                closeTimer?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void LoggingCheckBox_CheckedChanged(object? sender, EventArgs e)
        {
            UpdateViewLogButtonVisibility();
        }

        private void UpdateViewLogButtonVisibility()
        {
            if (viewLogButton != null && loggingCheckBox != null)
            {
                viewLogButton.Visible = loggingCheckBox.Checked;
            }
        }

        private void ViewLogButton_Click(object? sender, EventArgs e)
        {
            try
            {
                string logFilePath = MainForm.GetLogFilePath();
                if (File.Exists(logFilePath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo 
                    { 
                        FileName = logFilePath,
                        UseShellExecute = true
                    });
                }
                else
                {
                    MessageBox.Show($"Debug log file not found at:\n{logFilePath}\n\nStart using the app with logging enabled to generate log entries.", 
                        "Debug Log", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening debug log:\n{ex.Message}", 
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Sets or removes the application from Windows startup
        /// </summary>
        /// <param name="enable">True to enable startup, false to disable</param>
        private void SetStartupWithWindows(bool enable)
        {
            const string appName = "Battify";
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key == null)
                {
                    throw new Exception("Unable to access Windows startup registry key");
                }

                if (enable)
                {
                    // Add the application to startup
                    string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue(appName, $"\"{exePath}\"");
                    }
                }
                else
                {
                    // Remove the application from startup
                    if (key.GetValue(appName) != null)
                    {
                        key.DeleteValue(appName);
                    }
                }
            }
        }

        /// <summary>
        /// Checks if the application is currently set to start with Windows
        /// </summary>
        /// <returns>True if startup is enabled, false otherwise</returns>
        private bool IsStartupWithWindowsEnabled()
        {
            const string appName = "Battify";
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    object? value = key.GetValue(appName);
                    return value != null;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
