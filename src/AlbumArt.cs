using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BlueAux
{
    // Cover art for the song an iPhone reports. The phone sends the title and artist only, so the art is
    // looked up in Apple's iTunes catalogue (no key, no account; Deezer as a fallback). Each song is looked
    // up once: results stay in memory and on disk, filed under a hash so no song names are written anywhere.
    sealed class AlbumArt
    {
        public sealed class Art
        {
            public BitmapSource Image;
            // The art shrunk to a few pixels: stretched over the card it makes a soft tint in the song's colours.
            public BitmapSource Ambient;
        }

        const int MaxCached = 40;

        readonly Dispatcher dispatcher;
        readonly Dictionary<string, Art> cache = new Dictionary<string, Art>();
        readonly List<string> order = new List<string>();
        readonly HashSet<string> pending = new HashSet<string>();
        readonly string folder;

        public event Action Changed;

        public AlbumArt(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            folder = Path.Combine(AppSettings.Folder, "art");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        // The art for a song, or null while it's being fetched (Changed fires when it arrives) or when
        // there is none. Call it as often as you like; a song is only ever looked up once.
        public Art Lookup(string title, string artist)
        {
            string key = Key(title, artist);
            Art art;
            if (cache.TryGetValue(key, out art)) return art;
            if (pending.Contains(key)) return null;
            pending.Add(key);
            Task.Run(() => Fetch(key, title, artist));
            return null;
        }

        void Fetch(string key, string title, string artist)
        {
            byte[] bytes = null;
            string file = Path.Combine(folder, key + ".jpg");
            try
            {
                if (File.Exists(file)) bytes = File.ReadAllBytes(file);
            }
            catch
            {
            }
            if (bytes == null)
            {
                try
                {
                    string url = FindOnItunes(title, artist) ?? FindOnDeezer(title, artist);
                    if (url != null) bytes = Download(url);
                }
                catch (Exception ex)
                {
                    Log.Info("Couldn't look up album art: " + ex.Message);
                }
                if (bytes != null)
                {
                    try
                    {
                        Directory.CreateDirectory(folder);
                        File.WriteAllBytes(file, bytes);
                    }
                    catch
                    {
                    }
                }
            }

            Art art = null;
            if (bytes != null)
            {
                try
                {
                    art = Decode(bytes);
                }
                catch (Exception ex)
                {
                    Log.Info("Couldn't read album art: " + ex.Message);
                }
            }
            dispatcher.BeginInvoke(new Action(() =>
            {
                pending.Remove(key);
                cache[key] = art;
                order.Add(key);
                while (order.Count > MaxCached)
                {
                    cache.Remove(order[0]);
                    order.RemoveAt(0);
                }
                var handler = Changed;
                if (handler != null) handler();
            }));
        }

        static string Key(string title, string artist)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes((artist ?? "").ToLowerInvariant() + "\n" + (title ?? "").ToLowerInvariant()));
                var text = new StringBuilder();
                foreach (byte b in hash) text.Append(b.ToString("x2"));
                return text.ToString();
            }
        }

        // iTunes Search: the catalogue iPhones play from, so it matches what the phone shows.
        static string FindOnItunes(string title, string artist)
        {
            string term = Uri.EscapeDataString(((artist ?? "") + " " + title).Trim());
            var json = Get("https://itunes.apple.com/search?term=" + term + "&entity=song&limit=5");
            var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            var results = root == null ? null : root["results"] as object[];
            if (results == null || results.Length == 0) return null;
            Dictionary<string, object> best = null;
            int bestScore = -1;
            foreach (var item in results)
            {
                var song = item as Dictionary<string, object>;
                if (song == null) continue;
                int score = Score(Text(song, "trackName"), title) * 2 + Score(Text(song, "artistName"), artist);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = song;
                }
            }
            string url = best == null ? null : Text(best, "artworkUrl100");
            // The catalogue serves any size; 300 px is plenty for the card and keeps the download small.
            return string.IsNullOrEmpty(url) ? null : url.Replace("100x100bb", "300x300bb");
        }

        static string FindOnDeezer(string title, string artist)
        {
            string q = Uri.EscapeDataString("artist:\"" + (artist ?? "") + "\" track:\"" + title + "\"");
            var json = Get("https://api.deezer.com/search?q=" + q + "&limit=1");
            var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            var data = root == null ? null : root["data"] as object[];
            if (data == null || data.Length == 0) return null;
            var song = data[0] as Dictionary<string, object>;
            var album = song == null ? null : song["album"] as Dictionary<string, object>;
            string url = album == null ? null : Text(album, "cover_medium");
            return string.IsNullOrEmpty(url) ? null : url;
        }

        static int Score(string found, string wanted)
        {
            if (string.IsNullOrEmpty(found) || string.IsNullOrEmpty(wanted)) return 0;
            if (string.Equals(found, wanted, StringComparison.OrdinalIgnoreCase)) return 3;
            if (found.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 || wanted.IndexOf(found, StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return 0;
        }

        static string Text(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj.TryGetValue(key, out value) ? value as string : null;
        }

        static string Get(string url)
        {
            return Encoding.UTF8.GetString(Download(url));
        }

        static byte[] Download(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.UserAgent = AppInfo.Name + "/" + AppInfo.Version;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        static Art Decode(byte[] bytes)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return new Art { Image = image, Ambient = Shrink(image, 4) };
        }

        // Box-averages the art down to n x n pixels; drawn big and smooth, that's the card's tint.
        static BitmapSource Shrink(BitmapSource source, int n)
        {
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);
            var small = new byte[n * n * 4];
            for (int cy = 0; cy < n; cy++)
            {
                for (int cx = 0; cx < n; cx++)
                {
                    int x0 = cx * w / n, x1 = Math.Max(x0 + 1, (cx + 1) * w / n);
                    int y0 = cy * h / n, y1 = Math.Max(y0 + 1, (cy + 1) * h / n);
                    long b = 0, g = 0, r = 0, count = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            int i = y * stride + x * 4;
                            b += pixels[i];
                            g += pixels[i + 1];
                            r += pixels[i + 2];
                            count++;
                        }
                    }
                    int o = (cy * n + cx) * 4;
                    small[o] = (byte)(b / count);
                    small[o + 1] = (byte)(g / count);
                    small[o + 2] = (byte)(r / count);
                    small[o + 3] = 255;
                }
            }
            var result = BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, small, n * 4);
            result.Freeze();
            return result;
        }
    }
}
