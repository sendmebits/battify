using System;
using System.Collections.Generic;
using System.Drawing;
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

        private CheckedListBox? deviceListBox;
        private NumericUpDown? thresholdNumeric;
        private NumericUpDown? intervalNumeric;
        private NumericUpDown? checkIntervalNumeric;
        private CheckBox? loggingCheckBox;
        private CheckBox? startWithWindowsCheckBox;
        private Button? viewLogButton;
        private Button? saveButton;
        private Button? cancelButton;
        private Button? refreshButton;
        private Label? statusLabel;
        private System.Windows.Forms.Timer? statusTimer;

        public event EventHandler? SettingsSaved;

        public SettingsForm(Settings settings, MainForm mainForm)
        {
            this.settings = settings;
            this.mainForm = mainForm;
            
            // Subscribe to the DevicesRefreshed event
            mainForm.DevicesRefreshed += OnDevicesRefreshed;
            this.FormClosed += (s, e) => mainForm.DevicesRefreshed -= OnDevicesRefreshed;
            
            InitializeComponent();
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
                
                // Reset timer
                statusTimer?.Stop();
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

        private void InitializeComponent()
        {
            this.Text = "Battify Settings";
            this.Size = new Size(350, 520);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            // Main panel
            var mainPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(15)
            };

            int yPos = 10;

            // Title
            var titleLabel = new Label
            {
                Text = "Battery Monitoring Settings",
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                Location = new Point(10, yPos),
                AutoSize = true
            };
            mainPanel.Controls.Add(titleLabel);
            yPos += 35;

            // Device selection section
            var deviceSectionLabel = new Label
            {
                Text = "Select Devices to Monitor:",
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Location = new Point(10, yPos),
                AutoSize = true
            };
            mainPanel.Controls.Add(deviceSectionLabel);
            yPos += 25;

            // Device list with checkboxes
            deviceListBox = new CheckedListBox
            {
                Location = new Point(10, yPos),
                Size = new Size(310, 120),
                CheckOnClick = true,
                DrawMode = DrawMode.OwnerDrawFixed
            };
            deviceListBox.DrawItem += DeviceListBox_DrawItem;
            deviceListBox.ItemCheck += (s, e) => 
            {
                var item = deviceListBox.Items[e.Index] as DeviceItem;
                if (item != null && string.IsNullOrEmpty(item.DeviceId))
                {
                    // Cancel the check if this is the "No devices found" message
                    e.NewValue = CheckState.Unchecked;
                }
            };
            mainPanel.Controls.Add(deviceListBox);
            yPos += 125;

            // Refresh button
            refreshButton = new Button
            {
                Text = "Refresh Devices",
                Location = new Point(10, yPos),
                Size = new Size(120, 25)
            };
            refreshButton.Click += RefreshButton_Click;
            mainPanel.Controls.Add(refreshButton);
            
            // Status label (inline with refresh button)
            statusLabel = new Label
            {
                Text = "",
                Location = new Point(135, yPos + 4),
                Size = new Size(185, 20),
                ForeColor = Color.Green,
                Font = new Font("Segoe UI", 9, FontStyle.Italic),
                Visible = false
            };
            mainPanel.Controls.Add(statusLabel);
            yPos += 35;

            // Battery threshold section
            var thresholdLabel = new Label
            {
                Text = "Low Battery Threshold (%):",
                Location = new Point(10, yPos),
                AutoSize = true
            };
            mainPanel.Controls.Add(thresholdLabel);

            thresholdNumeric = new NumericUpDown
            {
                Location = new Point(220, yPos - 3),
                Size = new Size(70, 23),
                Minimum = 1,
                Maximum = 100,
                Value = 20
            };
            mainPanel.Controls.Add(thresholdNumeric);

            var thresholdHelpButton = new Button
            {
                Text = "?",
                Location = new Point(295, yPos - 3),
                Size = new Size(23, 23),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                FlatStyle = FlatStyle.System
            };
            thresholdHelpButton.Click += (s, e) => 
            {
                MessageBox.Show(
                    "This setting determines when you'll receive low battery notifications.\n\n" +
                    "When a device's battery level drops below this percentage, " +
                    "Battify will show a notification to alert you.",
                    "Low Battery Threshold",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.None);
            };
            mainPanel.Controls.Add(thresholdHelpButton);
            yPos += 35;

            // Notification interval section
            var intervalLabel = new Label
            {
                Text = "Notification Repeat Interval (min):",
                Location = new Point(10, yPos),
                AutoSize = true
            };
            mainPanel.Controls.Add(intervalLabel);

            intervalNumeric = new NumericUpDown
            {
                Location = new Point(220, yPos - 3),
                Size = new Size(70, 23),
                Minimum = 1,
                Maximum = 1440,
                Value = 30
            };
            mainPanel.Controls.Add(intervalNumeric);

            var intervalHelpButton = new Button
            {
                Text = "?",
                Location = new Point(295, yPos - 3),
                Size = new Size(23, 23),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                FlatStyle = FlatStyle.System
            };
            intervalHelpButton.Click += (s, e) => 
            {
                MessageBox.Show(
                    "This controls how often you'll be reminded about low battery devices.\n\n" +
                    "If a device remains below the battery threshold, Battify will show " +
                    "another notification after this many minutes have passed since the last notification.",
                    "Notification Repeat Interval",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.None);
            };
            mainPanel.Controls.Add(intervalHelpButton);
            yPos += 35;

            // Check interval section
            var checkLabel = new Label
            {
                Text = "Battery Check Interval (min):",
                Location = new Point(10, yPos),
                AutoSize = true
            };
            mainPanel.Controls.Add(checkLabel);

            checkIntervalNumeric = new NumericUpDown
            {
                Location = new Point(220, yPos - 3),
                Size = new Size(70, 23),
                Minimum = 1,
                Maximum = 60,
                Value = 1
            };
            mainPanel.Controls.Add(checkIntervalNumeric);

            var checkHelpButton = new Button
            {
                Text = "?",
                Location = new Point(295, yPos - 3),
                Size = new Size(23, 23),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                FlatStyle = FlatStyle.System
            };
            checkHelpButton.Click += (s, e) => 
            {
                MessageBox.Show(
                    "This determines how frequently Battify checks the battery levels of your devices.\n\n" +
                    "More frequent checks (lower values) provide more up-to-date information but " +
                    "may use slightly more system resources.",
                    "Battery Check Interval",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.None);
            };
            mainPanel.Controls.Add(checkHelpButton);
            yPos += 45;

            // Start with Windows section
            startWithWindowsCheckBox = new CheckBox
            {
                Text = "Start automatically with Windows",
                Location = new Point(10, yPos),
                AutoSize = true
            };
            mainPanel.Controls.Add(startWithWindowsCheckBox);
            yPos += 30;

            // Logging section
            loggingCheckBox = new CheckBox
            {
                Text = "Enable Debug Logging",
                Location = new Point(10, yPos),
                AutoSize = true
            };
            loggingCheckBox.CheckedChanged += LoggingCheckBox_CheckedChanged;
            mainPanel.Controls.Add(loggingCheckBox);
            yPos += 30;

            // View Log button
            viewLogButton = new Button
            {
                Text = "View Debug Log",
                Location = new Point(10, yPos),
                Size = new Size(120, 25),
                Visible = false
            };
            viewLogButton.Click += ViewLogButton_Click;
            mainPanel.Controls.Add(viewLogButton);
            yPos += 35;

            // Buttons
            saveButton = new Button
            {
                Text = "Save",
                Location = new Point(150, yPos),
                Size = new Size(80, 30),
                DialogResult = DialogResult.OK
            };
            saveButton.Click += SaveButton_Click;
            mainPanel.Controls.Add(saveButton);

            cancelButton = new Button
            {
                Text = "Cancel",
                Location = new Point(240, yPos),
                Size = new Size(80, 30),
                DialogResult = DialogResult.Cancel
            };
            mainPanel.Controls.Add(cancelButton);

            this.Controls.Add(mainPanel);
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
                checkIntervalNumeric.Value = settings.CheckIntervalMinutes;

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
            if (deviceListBox == null) return;
            
            // Get current device data from MainForm
            var availableDevices = mainForm.ConnectedDevices;
            var currentBatteryLevels = mainForm.LastKnownBatteryLevels;
            
            // Save currently checked device IDs before clearing
            var checkedDeviceIds = new HashSet<string>();
            foreach (var item in deviceListBox.CheckedItems)
            {
                var deviceItem = item as DeviceItem;
                if (deviceItem != null)
                {
                    checkedDeviceIds.Add(deviceItem.DeviceId);
                }
            }
            
            // Also include devices from settings (for initial load)
            foreach (var deviceId in settings.MonitoredDevices)
            {
                checkedDeviceIds.Add(deviceId);
            }
            
            deviceListBox.Items.Clear();
            
            // Create a dictionary to track which devices have been added
            var addedDeviceIds = new HashSet<string>();
            
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
                    string batteryInfo = currentBatteryLevels.ContainsKey(deviceName)
                        ? $" [{currentBatteryLevels[deviceName]}%]"
                        : " [Battery unknown]";

                    // Update the device name in settings for persistence
                    if (!string.IsNullOrEmpty(device.Name))
                    {
                        settings.DeviceNames[device.DeviceId] = device.Name;
                    }

                    int index = deviceListBox.Items.Add(new DeviceItem
                    {
                        DeviceId = device.DeviceId,
                        DisplayName = $"{deviceName}{batteryInfo}",
                        IsConnected = true
                    });
                    
                    addedDeviceIds.Add(device.DeviceId);
                    
                    // Restore checked state if this device was previously checked
                    if (checkedDeviceIds.Contains(device.DeviceId))
                    {
                        deviceListBox.SetItemChecked(index, true);
                    }
                }
            }
            
            // Now add any monitored devices that are not currently connected (greyed out)
            foreach (var deviceId in checkedDeviceIds)
            {
                if (!addedDeviceIds.Contains(deviceId))
                {
                    // This is a monitored device that's currently disconnected
                    // Use the persisted name if available, otherwise try to extract from ID
                    string deviceName = settings.DeviceNames.ContainsKey(deviceId)
                        ? settings.DeviceNames[deviceId]
                        : ExtractDeviceNameFromId(deviceId);
                    
                    int index = deviceListBox.Items.Add(new DeviceItem
                    {
                        DeviceId = deviceId,
                        DisplayName = $"{deviceName} [Disconnected]",
                        IsConnected = false
                    });
                    
                    // Keep it checked since it's in the monitored list
                    deviceListBox.SetItemChecked(index, true);
                }
            }
            
            // Show a message if there are no devices at all
            if (deviceListBox.Items.Count == 0)
            {
                deviceListBox.Items.Add(new DeviceItem
                {
                    DeviceId = "",
                    DisplayName = "No Bluetooth devices found",
                    IsConnected = false
                });
            }
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
        
        private void DeviceListBox_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || deviceListBox == null) return;
            
            var item = deviceListBox.Items[e.Index] as DeviceItem;
            if (item == null) return;
            
            // Draw the background
            e.DrawBackground();
            
            // Determine text color based on connection status and if it's the "No devices" message
            Color textColor;
            if (string.IsNullOrEmpty(item.DeviceId)) // This is the "No devices found" message
            {
                textColor = Color.Gray;
                // Draw text without checkbox offset since we won't show a checkbox
                using (SolidBrush brush = new SolidBrush(textColor))
                {
                    Rectangle textRect = e.Bounds;
                    StringFormat format = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    };
                    
                    e.Graphics.DrawString(
                        item.DisplayName,
                        e.Font ?? SystemFonts.DefaultFont,
                        brush,
                        textRect,
                        format);
                }
            }
            else
            {
                textColor = item.IsConnected ? e.ForeColor : Color.Gray;
                // Draw the checkbox (handled by the control itself) and text
                using (SolidBrush brush = new SolidBrush(textColor))
                {
                    // Offset for the checkbox
                    Rectangle textRect = new Rectangle(
                        e.Bounds.X + 18, 
                        e.Bounds.Y, 
                        e.Bounds.Width - 18, 
                        e.Bounds.Height);
                    
                    e.Graphics.DrawString(
                        item.DisplayName,
                        e.Font ?? SystemFonts.DefaultFont,
                        brush,
                        textRect,
                        StringFormat.GenericDefault);
                }
            }
            
            // Draw focus rectangle if needed
            e.DrawFocusRectangle();
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
                settings.CheckIntervalMinutes = (int)checkIntervalNumeric.Value;

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
            
            if (deviceListBox != null)
            {
                foreach (var item in deviceListBox.CheckedItems)
                {
                    var deviceItem = item as DeviceItem;
                    if (deviceItem != null)
                    {
                        settings.MonitoredDevices.Add(deviceItem.DeviceId);
                        newMonitoredDeviceIds.Add(deviceItem.DeviceId);
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
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                this.Close();
            };
            timer.Start();
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

        private class DeviceItem
        {
            public string DeviceId { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public bool IsConnected { get; set; } = true;

            public override string ToString() => DisplayName;
        }
    }
}
