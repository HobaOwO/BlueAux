using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace BlueAux
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
        public bool ToldAboutTray;
        public bool KeepAwake = true;
        public bool Notifications = true;
        public bool CloseToTray = true;
        // No longer shown: hidden devices are listed in Settings instead. Kept so old settings files load.
        public bool ShowHidden;
        public bool RestWhenIdle = true;
        public int IdleMinutes = 10;
        // Off (the default): a phone that's already connected stays connected while resting; only searching
        // stops. Disconnecting a paused iPhone and reconnecting it later is what most often leaves it silent.
        public bool DisconnectWhenIdle;
        public List<DeviceSettings> Devices = new List<DeviceSettings>();

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Id); }
        }

        static string FilePath
        {
            get { return Path.Combine(Folder, "settings.xml"); }
        }

        // After a rename, bring settings and logs over from the old folder (once).
        public static void MoveFromOldFolder()
        {
            if (Directory.Exists(Folder)) return;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (string oldId in AppInfo.OldIds)
            {
                if (oldId == AppInfo.Id) continue;
                string old = Path.Combine(appData, oldId);
                if (!File.Exists(Path.Combine(old, "settings.xml"))) continue;
                try
                {
                    Directory.CreateDirectory(Folder);
                    foreach (string file in Directory.GetFiles(old))
                        File.Copy(file, Path.Combine(Folder, Path.GetFileName(file)), true);
                }
                catch
                {
                }
                return;
            }
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
        const string ValueName = AppInfo.Id;

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

        // If the app was moved or renamed since start-up was turned on, point the entry at it again.
        public static void Repair()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    foreach (string oldId in AppInfo.OldIds)
                    {
                        if (oldId == ValueName || key.GetValue(oldId) == null) continue;
                        key.DeleteValue(oldId, false);
                        key.SetValue(ValueName, Command);
                    }
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
