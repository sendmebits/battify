using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Battify
{
    /// <summary>
    /// Application settings for battery monitoring
    /// </summary>
    public class Settings
    {
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Battify",
            "settings.json");

        /// <summary>
        /// List of device IDs to monitor
        /// </summary>
        public List<string> MonitoredDevices { get; set; } = new List<string>();

        /// <summary>
        /// Dictionary mapping device IDs to their friendly names
        /// </summary>
        public Dictionary<string, string> DeviceNames { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Battery threshold percentage (1-100). Notifications trigger when battery drops below this.
        /// </summary>
        public int BatteryThreshold { get; set; } = 20;

        /// <summary>
        /// Notification repeat interval in minutes
        /// </summary>
        public int NotificationIntervalMinutes { get; set; } = 30;

        /// <summary>
        /// Battery check interval in minutes
        /// </summary>
        public int CheckIntervalMinutes { get; set; } = 1;

        /// <summary>
        /// Enable debug logging
        /// </summary>
        public bool LoggingEnabled { get; set; } = false;

        /// <summary>
        /// Start application automatically with Windows
        /// </summary>
        public bool StartWithWindows { get; set; } = false;

        /// <summary>
        /// Load settings from disk
        /// </summary>
        public static Settings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<Settings>(json);
                    return settings ?? new Settings();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading settings: {ex.Message}");
            }

            return new Settings();
        }

        /// <summary>
        /// Save settings to disk
        /// </summary>
        public void Save()
        {
            try
            {
                string directory = Path.GetDirectoryName(SettingsFilePath) ?? "";
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving settings: {ex.Message}");
            }
        }
    }
}
