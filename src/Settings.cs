using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace PhoneSpeaker
{
    public sealed class DeviceSettings
    {
        public string Key;
        public string Nickname;
        public bool AutoConnect = true;
        public bool Hidden;
        public DateTime LastConnected;

        // True until we've looked the device up once, so the auto-connect default can follow its type.
        [XmlIgnore]
        public bool IsNew;
    }

    public sealed class AppSettings
    {
        static readonly XmlSerializer Serializer = new XmlSerializer(typeof(AppSettings));

        public bool AskedAboutStartup;
        public bool KeepAwake = true;
        public bool Notifications = true;
        public bool CloseToTray = true;
        public bool ShowHidden;
        public bool RestWhenIdle = true;
        public int IdleMinutes = 10;
        // Off: a phone that's already connected stays connected while resting; only searching stops.
        public bool DisconnectWhenIdle = true;
        public List<DeviceSettings> Devices = new List<DeviceSettings>();

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoneSpeaker"); }
        }

        static string FilePath
        {
            get { return Path.Combine(Folder, "settings.xml"); }
        }

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var stream = File.OpenRead(FilePath))
                        return (AppSettings)Serializer.Deserialize(stream);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't read settings, starting fresh", ex);
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                string temp = FilePath + ".tmp";
                using (var stream = File.Create(temp))
                    Serializer.Serialize(stream, this);
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                else File.Move(temp, FilePath);
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't save settings", ex);
            }
        }

        public DeviceSettings ForDevice(string key)
        {
            var existing = Devices.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;
            var created = new DeviceSettings { Key = key, IsNew = true };
            Devices.Add(created);
            return created;
        }
    }

    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "PhoneSpeaker";

        static string Command
        {
            get { return "\"" + Assembly.GetExecutingAssembly().Location + "\" --minimized"; }
        }

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                        return key != null && key.GetValue(ValueName) != null;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static void Set(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled) key.SetValue(ValueName, Command);
                    else key.DeleteValue(ValueName, false);
                }
                Log.Info(enabled ? "Will start with Windows" : "Won't start with Windows");
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't change the start-with-Windows setting", ex);
            }
        }

        // If the app was moved since start-up was turned on, point the entry at the new location.
        public static void Repair()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    var current = key.GetValue(ValueName) as string;
                    if (current != null && current != Command) key.SetValue(ValueName, Command);
                }
            }
            catch
            {
            }
        }
    }
}
