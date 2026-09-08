using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MediaPorter
{
    /// <summary>Keeping the portable folder healthy: tool updates, making the
    /// APP folder self-contained, and rebuilding the exe from source.</summary>
    public static class Maintenance
    {
        // -----------------------------------------------------------------
        //  yt-dlp updates itself in place
        // -----------------------------------------------------------------
        public static void UpdateYtDlp(IJobSink sink)
        {
            string ytdlp = Tools.YtDlp;
            if (ytdlp == null) throw new FileNotFoundException("yt-dlp.exe not found.");

            sink.Stage("Updating yt-dlp");
            sink.Progress(-1);
            sink.Log("Using " + ytdlp);

            int code = Proc.Run(ytdlp, "-U", null, l => sink.Log(l), sink.Token);
            if (code != 0)
            {
                sink.Log("Self-update returned " + code + " - fetching a fresh copy instead...");
                DownloadYtDlp(Path.GetDirectoryName(ytdlp), sink);
            }
            sink.Log("yt-dlp is now " + Tools.Version(Tools.YtDlp, "--version"));
            sink.Stage("Done");
            sink.Progress(100);
        }

        /// <summary>Turns raw byte counts into a stage line and a moving bar. The stage
        /// text is only refreshed about twice a second - rewriting it on every 80 KB
        /// chunk would flood the UI thread for no visible benefit.</summary>
        static Action<long, long> Reporter(IJobSink sink, string what)
        {
            var last = new DateTime[1];
            return (got, total) =>
            {
                if (total > 0) sink.Progress(got * 100.0 / total);
                else sink.Progress(-1);

                DateTime now = DateTime.UtcNow;
                if ((now - last[0]).TotalMilliseconds < 500) return;
                last[0] = now;

                sink.Stage(total > 0
                    ? "Downloading " + what + "  " + Util.HumanSize(got) + " of " + Util.HumanSize(total)
                    : "Downloading " + what + "  " + Util.HumanSize(got));
            };
        }

        public static void DownloadYtDlp(string targetDir, IJobSink sink)
        {
            Directory.CreateDirectory(targetDir);
            string dest = Path.Combine(targetDir, "yt-dlp.exe");
            string tmp = dest + ".new";
            sink.Stage("Downloading yt-dlp");
            sink.Log("Downloading the latest yt-dlp from GitHub...");
            Util.Download(YtDlpLatest, tmp, 120000, Reporter(sink, "yt-dlp"));
            if (new FileInfo(tmp).Length < 1024 * 1024) throw new Exception("Downloaded yt-dlp looks truncated.");
            Util.TryDelete(dest);
            File.Move(tmp, dest);
            sink.Log("yt-dlp updated.");
        }

        // -----------------------------------------------------------------
        //  ffmpeg - grab the latest essentials build and keep just ffmpeg.exe
        // -----------------------------------------------------------------
        public const string YtDlpLatest = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        public const string YtDlpApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
        public const string FFmpegGitHub = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";
        public const string FFmpegMirror = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

        /// <summary>Compares the installed yt-dlp against the newest GitHub release.
        /// Returns null when it is current (or when the check could not run).</summary>
        public static string CheckYtDlpUpdate()
        {
            try
            {
                string local = Tools.Version(Tools.YtDlp, "--version").Trim();
                if (local.Length == 0) return null;

                string body = Util.Http(YtDlpApi, 8000, "application/vnd.github+json");
                string latest = Json.Str(Json.Parse(body), "tag_name").Trim();
                if (latest.Length == 0) return null;

                return string.Equals(local, latest, StringComparison.OrdinalIgnoreCase) ? null : latest;
            }
            catch { return null; }
        }

        public static void UpdateFFmpeg(IJobSink sink)
        {
            string targetDir = Directory.Exists(AppPaths.BinDir) && File.Exists(Path.Combine(AppPaths.BinDir, "ffmpeg.exe"))
                ? AppPaths.BinDir
                : (Tools.FFmpeg != null ? Path.GetDirectoryName(Tools.FFmpeg) : AppPaths.BinDir);

            Directory.CreateDirectory(targetDir);

            sink.Stage("Downloading ffmpeg");
            sink.Progress(-1);

            string zip = Path.Combine(Path.GetTempPath(), "ipodsuite_ffmpeg.zip");
            string extract = Path.Combine(Path.GetTempPath(), "ipodsuite_ffmpeg_" + Guid.NewGuid().ToString("N").Substring(0, 6));

            try
            {
                Util.TryDelete(zip);
                sink.Log("Fetching the latest ffmpeg build from GitHub...");
                try
                {
                    Util.Download(FFmpegGitHub, zip, 600000, Reporter(sink, "ffmpeg"));
                }
                catch (Exception ex)
                {
                    sink.Log("GitHub download failed (" + ex.Message + ") - trying the gyan.dev mirror...");
                    Util.TryDelete(zip);
                    Util.Download(FFmpegMirror, zip, 600000, Reporter(sink, "ffmpeg"));
                }

                sink.Stage("Unpacking ffmpeg");
                sink.Progress(-1);
                Directory.CreateDirectory(extract);
                ZipFile.ExtractToDirectory(zip, extract);

                int copied = 0;
                foreach (string wanted in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    string[] hits = Directory.GetFiles(extract, wanted, SearchOption.AllDirectories);
                    if (hits.Length == 0) continue;
                    string dest = Path.Combine(targetDir, wanted);
                    string bak = dest + ".old";
                    Util.TryDelete(bak);
                    try { if (File.Exists(dest)) File.Move(dest, bak); } catch { }
                    File.Copy(hits[0], dest, true);
                    Util.TryDelete(bak);
                    copied++;
                    sink.Log("Installed " + wanted);
                }

                if (copied == 0) throw new Exception("The archive did not contain ffmpeg.exe.");
                sink.Log("ffmpeg is now " + Tools.Version(Tools.FFmpeg, "-version"));
                sink.Stage("Done");
                sink.Progress(100);
            }
            finally
            {
                Util.TryDelete(zip);
                try { Directory.Delete(extract, true); } catch { }
            }
        }

        // -----------------------------------------------------------------
        //  Make the APP folder fully self-contained
        // -----------------------------------------------------------------
        public static void MakePortable(Config cfg, IJobSink sink)
        {
            sink.Stage("Copying tools into APP\\bin");
            sink.Progress(-1);
            Directory.CreateDirectory(AppPaths.BinDir);

            int n = 0;
            foreach (string exe in new[] { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe", "node.exe" })
            {
                string src = Tools.Find(exe);
                if (src == null) continue;
                string dest = Path.Combine(AppPaths.BinDir, exe);
                if (string.Equals(src, dest, StringComparison.OrdinalIgnoreCase)) { n++; continue; }
                try { File.Copy(src, dest, true); n++; sink.Log("Copied " + exe); }
                catch (Exception ex) { sink.Log("Could not copy " + exe + ": " + ex.Message); }
            }

            if (Tools.Find("yt-dlp.exe") == null)
            {
                sink.Log("yt-dlp is missing - fetching it (about 17 MB)...");
                DownloadYtDlp(AppPaths.BinDir, sink);
                n++;
            }
            if (Tools.Find("ffmpeg.exe") == null)
            {
                sink.Log("ffmpeg is missing - fetching it (about 100 MB, this is the slow one)...");
                UpdateFFmpeg(sink);
                n++;
            }

            sink.Log(n + " tool(s) now live inside the APP folder. You can copy APP to any PC.");
            sink.Stage("Done");
            sink.Progress(100);
        }

        // -----------------------------------------------------------------
        //  Rebuild the exe from the bundled C# source
        // -----------------------------------------------------------------
        public static bool CanRebuild()
        {
            // Replacing the running exe means writing into its own folder, which a
            // Program Files install will not allow without elevation.
            return AppPaths.AppDirWritable
                && File.Exists(CscPath())
                && File.Exists(Path.Combine(AppPaths.SourceDir, "build.bat"));
        }

        public static string CscPath()
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string x64 = Path.Combine(win, @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
            if (File.Exists(x64)) return x64;
            return Path.Combine(win, @"Microsoft.NET\Framework\v4.0.30319\csc.exe");
        }

        /// <summary>Builds to a temporary exe, then hands the swap-and-restart to a
        /// small batch file (a running exe cannot overwrite itself).</summary>
        public static void Rebuild(IJobSink sink, bool restart)
        {
            string build = Path.Combine(AppPaths.SourceDir, "build.bat");
            if (!File.Exists(build))
                throw new FileNotFoundException("source\\build.bat is missing - the source folder was not copied along.");
            if (!File.Exists(CscPath()))
                throw new FileNotFoundException("The Windows C# compiler was not found on this PC.");

            string current = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string staged = Path.Combine(AppPaths.DataDir, "MediaPorter.staged.exe");
            Util.TryDelete(staged);

            sink.Stage("Compiling");
            sink.Progress(-1);
            sink.Log("Compiler: " + CscPath());

            int code = Proc.Run(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                "/c \"\"" + build + "\" \"" + staged + "\"\"",
                AppPaths.SourceDir,
                l => { if (l.Trim().Length > 0) sink.Log(l); },
                sink.Token);

            if (code != 0 || !File.Exists(staged))
                throw new Exception("Build failed (exit " + code + "). Nothing was replaced - the app you are using is untouched.");

            sink.Log("Build succeeded: " + Util.HumanSize(new FileInfo(staged).Length));

            if (!restart)
            {
                sink.Log("The new build is waiting at " + staged);
                sink.Stage("Done");
                sink.Progress(100);
                return;
            }

            string swap = Path.Combine(AppPaths.DataDir, "apply_update.cmd");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("ping 127.0.0.1 -n 3 >nul");
            sb.AppendLine("copy /y \"" + staged + "\" \"" + current + "\" >nul");
            sb.AppendLine("if exist \"" + Path.Combine(AppPaths.SourceDir, "app.config") + "\" copy /y \"" +
                          Path.Combine(AppPaths.SourceDir, "app.config") + "\" \"" + current + ".config\" >nul");
            sb.AppendLine("del \"" + staged + "\" >nul 2>&1");
            sb.AppendLine("start \"\" \"" + current + "\"");
            sb.AppendLine("del \"%~f0\" >nul 2>&1");
            File.WriteAllText(swap, sb.ToString(), Encoding.Default);

            sink.Log("Restarting into the new build...");
            Process.Start(new ProcessStartInfo(swap) { UseShellExecute = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            App.RequestShutdown();
        }

        // -----------------------------------------------------------------
        //  Status card for the settings page
        // -----------------------------------------------------------------
        public static List<string[]> ToolReport(Config cfg)
        {
            var rows = new List<string[]>();
            rows.Add(Row("yt-dlp", Tools.YtDlp, "--version"));
            rows.Add(Row("ffmpeg", Tools.FFmpeg, "-version"));
            rows.Add(Row("node (YouTube signatures)", Tools.Find("node.exe"), "--version"));

            var gpuBits = new List<string>();
            if (Gpus.HasNvidia) gpuBits.Add("NVENC");
            if (Gpus.HasAmd) gpuBits.Add("AMF");
            if (Gpus.HasIntel) gpuBits.Add("Quick Sync");
            rows.Add(new[] {
                "graphics",
                Gpus.Describe() + (gpuBits.Count > 0 ? "  ->  " + string.Join(" / ", gpuBits.ToArray()) : ""),
                "" });

            string it = ITunesSync.ITunesInstalled()
                ? (ITunesSync.ITunesRunning() ? "installed, running" : "installed, not running")
                : "not installed";
            rows.Add(new[] { "iTunes", it, "" });
            return rows;
        }

        static string[] Row(string name, string path, string versionArg)
        {
            if (path == null) return new[] { name, "missing", "" };
            string v = "";
            try
            {
                v = Tools.Version(path, versionArg);
                if (v.Length > 60) v = v.Substring(0, 60);
            }
            catch { }
            return new[] { name, v.Length > 0 ? v : "found", path };
        }
    }
}
