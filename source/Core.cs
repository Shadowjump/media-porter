using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MediaPorter
{
    // =====================================================================
    //  Paths: everything the app needs is resolved relative to the EXE so the
    //  APP folder can be copied to any PC / USB stick and still work.
    // =====================================================================
    public static class AppPaths
    {
        public static string AppDir { get; private set; }
        public static string UiDir { get { return Path.Combine(AppDir, "ui"); } }
        public static string BinDir { get { return Path.Combine(AppDir, "bin"); } }
        public static string DataDir { get { return Path.Combine(AppDir, "data"); } }
        public static string SourceDir { get { return Path.Combine(AppDir, "source"); } }
        public static string ConfigFile { get { return Path.Combine(DataDir, "config.json"); } }
        public static string LogFile { get { return Path.Combine(DataDir, "app.log"); } }

        static AppPaths()
        {
            AppDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            Directory.CreateDirectory(DataDir);
        }

        public static string Today { get { return DateTime.Now.ToString("yyyy-MM-dd"); } }

        /// <summary>Best guess at where the MediaPorter media library lives.</summary>
        public static string DetectSuiteRoot()
        {
            // 1. A suite sitting next to / above the APP folder (portable layout)
            string probe = AppDir;
            for (int i = 0; i < 4 && probe != null; i++)
            {
                if (LooksLikeSuite(probe)) return probe;
                string sib = Path.Combine(probe, "MediaPorter");
                if (LooksLikeSuite(sib)) return sib;
                probe = Path.GetDirectoryName(probe);
            }

            // 2. The original suite on this machine
            string desktop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MediaPorter");
            if (LooksLikeSuite(desktop)) return desktop;

            // 3. Self-contained: keep the whole library inside APP\Library
            return Path.Combine(AppDir, "Library");
        }

        public static bool LooksLikeSuite(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            return Directory.Exists(Path.Combine(dir, "02_Sync_Music"))
                || Directory.Exists(Path.Combine(dir, "02_Sync_Videos"))
                || Directory.Exists(Path.Combine(dir, "03_Tools_Binaries"));
        }
    }

    // =====================================================================
    //  Config
    // =====================================================================
    public class Config
    {
        public string SuiteRoot = "";
        public string AudioBitrate = "192k";
        public double Loudness = -16.0;
        public double TruePeak = -1.5;
        public bool Normalize = true;
        public bool EnrichMetadata = true;
        public bool EmbedArtwork = true;
        // --- Video: fixed at what the iPod nano 7G actually is -------------
        // Apple's spec: 240x432 screen, H.264 Baseline/Main/High profile level 3.0,
        // AAC-LC up to 256 kbps. So: encode at the exact screen size in landscape
        // and use High profile, which the device decodes and which is roughly a
        // quarter smaller than Baseline at the same quality.
        // Quality-targeted (CRF) rather than a fixed bitrate: a talking head stays
        // small, a busy music video gets the bits it needs, both look the same.
        // Which Apple device we are encoding for. Everything about the video -
        // size, frame-rate ceiling, profile, level, bitrate, codec - comes from
        // ui\devices.json via this id.
        public string DeviceId = "ipod-nano-7g";
        public bool PreferHevc = false;
        public bool UseGpu = true;                 // GPU first, CPU only as a fallback

        DeviceProfile _device;
        public DeviceProfile Device
        {
            get
            {
                if (_device == null || _device.Id != DeviceId) _device = Devices.Find(DeviceId);
                return _device;
            }
        }
        public bool AutoSyncAfterDownload = false;
        public bool DeleteFromLibraryAfterTransfer = true;
        public bool AutoUpdateTools = true;
        public bool WarnWhenDeviceMissing = true;
        public string LastToolCheck = "";

        // Where downloads land. Empty means the standard Incoming folder inside the
        // library; set one of these and that media type goes somewhere else instead.
        public string MusicIncomingOverride = "";
        public string VideoIncomingOverride = "";

        public string MusicRoot { get { return Path.Combine(SuiteRoot, "02_Sync_Music"); } }
        public string VideoRoot { get { return Path.Combine(SuiteRoot, "02_Sync_Videos"); } }
        public string MusicIncomingDefault { get { return Path.Combine(MusicRoot, "Incoming"); } }
        public string VideoIncomingDefault { get { return Path.Combine(VideoRoot, "Incoming"); } }

        public string MusicIncoming
        {
            get
            {
                return string.IsNullOrWhiteSpace(MusicIncomingOverride)
                    ? MusicIncomingDefault : MusicIncomingOverride;
            }
        }

        public string VideoIncoming
        {
            get
            {
                return string.IsNullOrWhiteSpace(VideoIncomingOverride)
                    ? VideoIncomingDefault : VideoIncomingOverride;
            }
        }

        public bool MusicIncomingMoved { get { return !string.IsNullOrWhiteSpace(MusicIncomingOverride); } }
        public bool VideoIncomingMoved { get { return !string.IsNullOrWhiteSpace(VideoIncomingOverride); } }
        public string MusicSynced { get { return Path.Combine(MusicRoot, "Synced"); } }
        public string VideoSynced { get { return Path.Combine(VideoRoot, "Synced"); } }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (File.Exists(AppPaths.ConfigFile))
                {
                    object j = Json.Parse(File.ReadAllText(AppPaths.ConfigFile, Encoding.UTF8));
                    c.SuiteRoot = Json.Str(j, "suiteRoot", "");
                    c.AudioBitrate = Json.Str(j, "audioBitrate", c.AudioBitrate);
                    c.Loudness = Json.Num(j, "loudness", c.Loudness);
                    c.TruePeak = Json.Num(j, "truePeak", c.TruePeak);
                    c.Normalize = Json.Bool(j, "normalize", c.Normalize);
                    c.EnrichMetadata = Json.Bool(j, "enrichMetadata", c.EnrichMetadata);
                    c.EmbedArtwork = Json.Bool(j, "embedArtwork", c.EmbedArtwork);
                    c.DeviceId = Json.Str(j, "deviceId", c.DeviceId);
                    c.PreferHevc = Json.Bool(j, "preferHevc", c.PreferHevc);
                    c.UseGpu = Json.Bool(j, "useGpu", Json.Bool(j, "useNvenc", c.UseGpu));
                    c.AutoSyncAfterDownload = Json.Bool(j, "autoSyncAfterDownload", false);
                    c.DeleteFromLibraryAfterTransfer = Json.Bool(j, "deleteFromLibraryAfterTransfer", true);
                    c.AutoUpdateTools = Json.Bool(j, "autoUpdateTools", true);
                    c.WarnWhenDeviceMissing = Json.Bool(j, "warnWhenDeviceMissing", true);
                    c.LastToolCheck = Json.Str(j, "lastToolCheck", "");
                    c.MusicIncomingOverride = Json.Str(j, "musicIncomingOverride", "");
                    c.VideoIncomingOverride = Json.Str(j, "videoIncomingOverride", "");
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(c.SuiteRoot) || !Directory.Exists(c.SuiteRoot))
                c.SuiteRoot = AppPaths.DetectSuiteRoot();

            c.EnsureFolders();
            return c;
        }

        public void EnsureFolders()
        {
            try
            {
                Directory.CreateDirectory(MusicIncoming);
                Directory.CreateDirectory(VideoIncoming);
                Directory.CreateDirectory(MusicSynced);
                Directory.CreateDirectory(VideoSynced);
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                var d = new Dictionary<string, object>();
                d["suiteRoot"] = SuiteRoot;
                d["audioBitrate"] = AudioBitrate;
                d["loudness"] = Loudness;
                d["truePeak"] = TruePeak;
                d["normalize"] = Normalize;
                d["enrichMetadata"] = EnrichMetadata;
                d["embedArtwork"] = EmbedArtwork;
                d["deviceId"] = DeviceId;
                d["preferHevc"] = PreferHevc;
                d["useGpu"] = UseGpu;
                d["autoSyncAfterDownload"] = AutoSyncAfterDownload;
                d["deleteFromLibraryAfterTransfer"] = DeleteFromLibraryAfterTransfer;
                d["autoUpdateTools"] = AutoUpdateTools;
                d["warnWhenDeviceMissing"] = WarnWhenDeviceMissing;
                d["lastToolCheck"] = LastToolCheck;
                d["musicIncomingOverride"] = MusicIncomingOverride;
                d["videoIncomingOverride"] = VideoIncomingOverride;
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(AppPaths.ConfigFile, Json.Write(d), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // =====================================================================
    //  Tools (yt-dlp / ffmpeg / ffprobe)
    // =====================================================================
    public static class Tools
    {
        public static string YtDlp { get { return Find("yt-dlp.exe"); } }
        public static string FFmpeg { get { return Find("ffmpeg.exe"); } }
        public static string FFprobe { get { return Find("ffprobe.exe"); } }

        public static string ToolsDir(Config cfg)
        {
            string local = AppPaths.BinDir;
            if (File.Exists(Path.Combine(local, "ffmpeg.exe"))) return local;
            if (cfg != null)
            {
                string suite = Path.Combine(cfg.SuiteRoot, "03_Tools_Binaries");
                if (File.Exists(Path.Combine(suite, "ffmpeg.exe"))) return suite;
            }
            return local;
        }

        public static string Find(string exe)
        {
            var candidates = new List<string>();
            candidates.Add(Path.Combine(AppPaths.BinDir, exe));

            var cfg = App.Cfg;
            if (cfg != null)
            {
                candidates.Add(Path.Combine(cfg.SuiteRoot, "03_Tools_Binaries", exe));
                candidates.Add(Path.Combine(cfg.SuiteRoot, exe));
            }
            candidates.Add(Path.Combine(AppPaths.AppDir, exe));

            foreach (string c in candidates)
                if (File.Exists(c)) return c;

            // Fall back to PATH
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string p in pathVar.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    string full = Path.Combine(p.Trim(), exe);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Does this ffmpeg build expose the NVIDIA encoder? Asked once.
        /// Whether the GPU actually accepts the job is settled by trying it.</summary>
        static string _encoders;

        public static bool HasNvenc(string ffmpegPath) { return HasNvenc(ffmpegPath, false); }

        public static bool HasNvenc(string ffmpegPath, bool hevc)
        {
            if (_encoders == null)
            {
                try { _encoders = Proc.Capture(ffmpegPath, "-hide_banner -encoders"); }
                catch { _encoders = ""; }
            }
            return HasEncoder(ffmpegPath, hevc ? "hevc_nvenc" : "h264_nvenc");
        }

        /// <summary>AMD's AMF encoder - the fallback for machines without NVIDIA.</summary>
        public static bool HasAmf(string ffmpegPath, bool hevc)
        {
            return HasEncoder(ffmpegPath, hevc ? "hevc_amf" : "h264_amf");
        }

        /// <summary>Intel Quick Sync.</summary>
        public static bool HasQsv(string ffmpegPath, bool hevc)
        {
            return HasEncoder(ffmpegPath, hevc ? "hevc_qsv" : "h264_qsv");
        }

        static bool HasEncoder(string ffmpegPath, string name)
        {
            if (_encoders == null)
            {
                try { _encoders = Proc.Capture(ffmpegPath, "-hide_banner -encoders"); }
                catch { _encoders = ""; }
            }
            return _encoders.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool NodeAvailable()
        {
            return Find("node.exe") != null;
        }

        public static string Version(string exePath, string args)
        {
            try
            {
                var sb = new StringBuilder();
                Proc.Run(exePath, args, null, line => { if (sb.Length < 200) sb.AppendLine(line); }, null);
                string s = sb.ToString().Trim();
                int nl = s.IndexOf('\n');
                if (nl > 0) s = s.Substring(0, nl).Trim();
                return s;
            }
            catch { return ""; }
        }
    }

    // =====================================================================
    //  Process runner with live line output
    // =====================================================================
    public static class Proc
    {
        public static int Run(string exe, string args, string workDir,
                              Action<string> onLine, CancellationToken? token,
                              Action<Process> onStarted = null)
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                throw new FileNotFoundException("Required tool not found: " + (exe ?? "(null)"));

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = string.IsNullOrEmpty(workDir) ? Path.GetDirectoryName(exe) : workDir
            };

            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;

                var done = new ManualResetEventSlim(false);
                int streamsOpen = 2;

                DataReceivedEventHandler handler = (s, e) =>
                {
                    if (e.Data == null)
                    {
                        if (Interlocked.Decrement(ref streamsOpen) == 0) done.Set();
                        return;
                    }
                    if (onLine != null) onLine(e.Data);
                };

                p.OutputDataReceived += handler;
                p.ErrorDataReceived += handler;

                p.Start();
                if (onStarted != null) onStarted(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                while (!p.WaitForExit(150))
                {
                    if (token.HasValue && token.Value.IsCancellationRequested)
                    {
                        try { KillTree(p); } catch { }
                        throw new OperationCanceledException();
                    }
                }
                p.WaitForExit();
                done.Wait(1500);
                return p.ExitCode;
            }
        }

        public static void KillTree(Process p)
        {
            try
            {
                var killer = new ProcessStartInfo("taskkill.exe", "/PID " + p.Id + " /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(killer).WaitForExit(4000);
            }
            catch
            {
                try { p.Kill(); } catch { }
            }
        }

        /// <summary>Runs a tool and returns everything it printed (stdout+stderr).</summary>
        public static string Capture(string exe, string args, CancellationToken? token = null)
        {
            var sb = new StringBuilder();
            Run(exe, args, null, l => sb.AppendLine(l), token);
            return sb.ToString();
        }
    }

    // =====================================================================
    //  Small helpers
    // =====================================================================
    public static class Util
    {
        static readonly Regex Multispace = new Regex(@"\s+", RegexOptions.Compiled);

        // Keeps latin, greek, digits, spaces and a few safe symbols - mirrors the
        // sanitising the original PowerShell scripts did.
        static readonly Regex Disallowed = new Regex(
            @"[^a-zA-Z0-9\u0370-\u03ff\u1f00-\u1fff\s\-_()\[\]{}!@#\$%\^&\+=';,.~]", RegexOptions.Compiled);

        static readonly string[] JunkPatterns =
        {
            @"\s*[\[\(](Official\s+)?(Music\s+)?Video[\]\)]",
            @"\s*[\[\(]Official\s+Audio[\]\)]",
            @"\s*[\[\(]Official\s+Lyric\s+Video[\]\)]",
            @"\s*[\[\(]Lyric(s)?(\s+Video)?[\]\)]",
            @"\s*[\[\(]Audio[\]\)]",
            @"\s*[\[\(]HD[\]\)]",
            @"\s*[\[\(]HQ[\]\)]",
            @"\s*[\[\(]4K[\]\)]",
            @"\s*[\[\(]M/V[\]\)]",
            @"\s*[\[\(]MV[\]\)]",
            @"\s*[\[\(]Visualizer[\]\)]",
            @"\s*-\s*YouTube$"
        };

        public static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string s = Disallowed.Replace(text, "");
            s = Multispace.Replace(s, " ");
            return s.Trim();
        }

        public static string StripJunk(string title)
        {
            if (string.IsNullOrEmpty(title)) return "";
            string s = title;
            foreach (string p in JunkPatterns)
                s = Regex.Replace(s, p, "", RegexOptions.IgnoreCase);
            return s.Trim();
        }

        public static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "untitled";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, ' ');
            name = Multispace.Replace(name, " ").Trim().TrimEnd('.');
            if (name.Length > 120) name = name.Substring(0, 120).Trim();
            return string.IsNullOrWhiteSpace(name) ? "untitled" : name;
        }

        public static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; i < 500; i++)
            {
                string p = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(p)) return p;
            }
            return path;
        }

        public static string HumanSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double b = bytes;
            int u = 0;
            while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
            return b.ToString(b < 10 && u > 0 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[u];
        }

        public static bool LooksLikeUrl(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeUrl(string s)
        {
            s = (s ?? "").Trim().Trim('"');
            if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = "https://" + s;
            return s;
        }

        public static void OpenFolder(string path)
        {
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch { }
        }

        public static void RevealFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                else
                    OpenFolder(Path.GetDirectoryName(path));
            }
            catch { }
        }

        public static string Http(string url, int timeoutMs = 6000, string accept = null)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            }
            catch { }
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "MediaPorter/2.0 ( https://github.com/Shadowjump/media-porter )";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            if (accept != null) req.Accept = accept;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        public static void Download(string url, string destFile, int timeoutMs = 30000)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            }
            catch { }
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "MediaPorter/2.0";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(destFile))
                src.CopyTo(dst);
        }

        public static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static void TryDeleteGlob(string dir, string pattern)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir, pattern))
                    TryDelete(f);
            }
            catch { }
        }
    }
}
