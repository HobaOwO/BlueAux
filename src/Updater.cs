using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Threading;

namespace BlueAux
{
    // Keeps BlueAux up to date from GitHub: looks at the latest release (never a pre-release) soon after start
    // and every 6 hours, downloads its BlueAux.exe when it's newer, and swaps it in once nothing is playing.
    // Windows lets a running .exe be renamed, so the new one takes its place and the old one is deleted
    // at the next start.
    sealed class Updater
    {
        const string LatestUrl = "https://api.github.com/repos/HobaOwO/BlueAux/releases/latest";
        const string DownloadPrefix = AppInfo.RepoUrl + "/releases/download/";
        static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

        readonly AppSettings settings;
        readonly Func<bool> busy;
        readonly Action<string> restart;
        DateTime nextCheck = DateTime.UtcNow.AddMinutes(1);
        bool checking, gaveUp;
        string ready, readyVersion;

        static string Exe { get { return Assembly.GetExecutingAssembly().Location; } }
        static string Download { get { return Path.Combine(AppSettings.Folder, "BlueAux.update.exe"); } }

        // busy: true while swapping would interrupt something. restart: start the new .exe with these
        // arguments and quit.
        public Updater(AppSettings settings, Dispatcher ui, Func<bool> busy, Action<string> restart)
        {
            this.settings = settings;
            this.busy = busy;
            this.restart = restart;
            TryDelete(Exe + ".old");
            TryDelete(Download);
            new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (s, e) => Tick(ui), ui).Start();
        }

        void Tick(Dispatcher ui)
        {
            if (!settings.AutoUpdate || gaveUp || checking) return;
            if (ready != null)
            {
                if (!busy()) Apply();
                return;
            }
            if (DateTime.UtcNow < nextCheck) return;
            nextCheck = DateTime.UtcNow + CheckEvery;
            checking = true;
            Task.Run(() => Check()).ContinueWith(t => ui.BeginInvoke(new Action(() =>
            {
                checking = false;
                if (t.IsFaulted) Log.Info("Couldn't check for updates: {0}", t.Exception.GetBaseException().Message);
                else if (t.Result != null)
                {
                    ready = Download;
                    readyVersion = t.Result;
                    Log.Info("BlueAux {0} downloaded; it installs when nothing is playing", readyVersion);
                }
            })));
        }

        // Returns the new version once its .exe is downloaded, or null when this one is the latest.
        static string Check()
        {
            var release = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(
                System.Text.Encoding.UTF8.GetString(Get(LatestUrl, 10000)));
            string tag = release["tag_name"] as string ?? "";
            Version latest, current = new Version(AppInfo.Version);
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out latest) || latest <= current) return null;

            foreach (Dictionary<string, object> asset in (object[])release["assets"])
            {
                string url = asset["browser_download_url"] as string;
                if (!string.Equals(asset["name"] as string, "BlueAux.exe", StringComparison.OrdinalIgnoreCase)) continue;
                // Only from this repository's own releases, over https.
                if (url == null || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue;
                byte[] exe = Get(url, 120000);
                if (!LooksLikeExe(exe)) throw new InvalidDataException("the downloaded BlueAux.exe isn't a program");
                File.WriteAllBytes(Download, exe);
                return latest.ToString();
            }
            return null;
        }

        static bool LooksLikeExe(byte[] data)
        {
            return data.Length > 100 * 1024 && data[0] == 'M' && data[1] == 'Z';
        }

        void Apply()
        {
            string exe = Exe, old = exe + ".old";
            try
            {
                TryDelete(old);
                File.Move(exe, old);
                try
                {
                    File.Copy(ready, exe);
                }
                catch
                {
                    File.Move(old, exe);
                    throw;
                }
            }
            catch (Exception ex)
            {
                // Usually a folder Windows protects (Program Files): leave it to the user.
                gaveUp = true;
                Log.Error("Couldn't install BlueAux " + readyVersion + " (get it from GitHub instead)", ex);
                return;
            }
            Log.Info("Installed BlueAux {0}; restarting", readyVersion);
            restart("--updated");
        }

        static byte[] Get(string url, int timeoutMs)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.UserAgent = AppInfo.Name + "/" + AppInfo.Version;
            request.Accept = "application/vnd.github+json, application/octet-stream";
            using (var response = request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
