using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MediaPorter
{
    public interface IJobSink
    {
        void Log(string line);
        void Stage(string text);
        void Progress(double percent);   // 0-100, or -1 for "busy / unknown"
        CancellationToken Token { get; }
    }

    public class TrackInfo
    {
        public string Artist = "Unknown Artist";
        public string Title = "";
        public string Album = "YouTube Downloads";
        public string Genre = "Music";
        public string ArtworkUrl = null;
        public double Duration = 0;
        public double Fps = 0;
        public string Source = "YouTube";
    }

    // =====================================================================
    //  Music + video pipelines (ports of download_music_beta.ps1 and
    //  download_and_convert.ps1, with live progress)
    // =====================================================================
    public static class MediaJobs
    {
        static readonly Regex RxDownloadPct = new Regex(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
        static readonly Regex RxOutTime = new Regex(@"out_time_us=(\d+)", RegexOptions.Compiled);
        static readonly Regex RxOutTimeMs = new Regex(@"out_time_ms=(\d+)", RegexOptions.Compiled);

        static string YtFlags()
        {
            // Only ask yt-dlp for the node JS runtime when node is actually present.
            return Tools.NodeAvailable() ? "--js-runtimes node " : "";
        }

        static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "'") + "\""; }

        // -----------------------------------------------------------------
        //  MUSIC
        // -----------------------------------------------------------------
        public static string DownloadMusic(string url, Config cfg, IJobSink sink)
        {
            string ytdlp = Tools.YtDlp, ffmpeg = Tools.FFmpeg;
            if (ytdlp == null) throw new FileNotFoundException("yt-dlp.exe not found. Check Settings > Tools.");
            if (ffmpeg == null) throw new FileNotFoundException("ffmpeg.exe not found. Check Settings > Tools.");

            string tempDir = Path.Combine(Path.GetTempPath(), "MediaPorter", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);

            try
            {
                // ---- 1. Metadata from YouTube -------------------------------
                sink.Stage("Reading track info");
                sink.Progress(-1);

                var info = ProbeTrack(ytdlp, url, sink);
                sink.Log("YouTube: " + info.Artist + " - " + info.Title);

                // ---- 2. Enrich from iTunes / MusicBrainz ---------------------
                if (cfg.EnrichMetadata)
                {
                    sink.Stage("Matching official tags");
                    Enrich(info, sink);
                }

                info.Artist = Fallback(Util.Clean(info.Artist), "Unknown Artist");
                info.Title = Fallback(Util.Clean(info.Title), "downloaded_track");
                info.Album = Fallback(Util.Clean(info.Album), "YouTube Downloads");
                info.Genre = Fallback(Util.Clean(info.Genre), "Music");

                // ---- 3. Download audio --------------------------------------
                sink.Stage("Downloading audio");
                sink.Progress(0);

                string tempAudio = Path.Combine(tempDir, "audio.m4a");
                string dlArgs = YtFlags() +
                    "--no-playlist --extractor-args \"youtube:player_client=android,web\" --newline -N 8 -f ba --extract-audio --audio-format m4a " +
                    "--write-thumbnail --convert-thumbnails jpg --ffmpeg-location " + Q(Path.GetDirectoryName(ffmpeg)) +
                    " -o " + Q(tempAudio) + " " + Q(url);

                int code = Proc.Run(ytdlp, dlArgs, tempDir, line =>
                {
                    var m = RxDownloadPct.Match(line);
                    if (m.Success)
                        sink.Progress(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 0.75);
                    else if (line.Length > 0 && !line.StartsWith("[download] Destination"))
                        sink.Log(line);
                }, sink.Token);

                if (!File.Exists(tempAudio) || new FileInfo(tempAudio).Length < 20 * 1024)
                    throw new Exception("yt-dlp could not download the audio (exit " + code + ").");

                // ---- 4. Cover art -------------------------------------------
                sink.Stage("Preparing cover art");
                sink.Progress(80);
                string cover = cfg.EmbedArtwork ? BuildCover(ffmpeg, tempDir, info, sink) : null;

                // ---- 5. Encode ----------------------------------------------
                sink.Stage(cfg.Normalize ? "Normalising & encoding" : "Encoding");
                sink.Progress(85);

                string outDir = Path.Combine(cfg.MusicIncoming, AppPaths.Today);
                Directory.CreateDirectory(outDir);
                string target = Util.UniquePath(Path.Combine(outDir,
                    Util.SafeFileName(info.Artist + " - " + info.Title) + ".m4a"));
                string tempOut = Path.Combine(tempDir, "out.m4a");

                var a = new StringBuilder();
                a.Append("-y -i ").Append(Q(tempAudio));
                if (cover != null) a.Append(" -i ").Append(Q(cover)).Append(" -map 0:a -map 1:v");
                a.Append(" -c:a aac -b:a ").Append(cfg.AudioBitrate).Append(" -ar 44100");
                if (cfg.Normalize)
                    a.Append(" -filter:a loudnorm=I=").Append(N(cfg.Loudness))
                     .Append(":TP=").Append(N(cfg.TruePeak)).Append(":LRA=11");
                if (cover != null) a.Append(" -c:v copy -disposition:v:0 attached_pic");
                a.Append(" -metadata title=").Append(Q(info.Title));
                a.Append(" -metadata artist=").Append(Q(info.Artist));
                a.Append(" -metadata album_artist=").Append(Q(info.Artist));
                a.Append(" -metadata album=").Append(Q(info.Album));
                a.Append(" -metadata genre=").Append(Q(info.Genre));
                a.Append(" -progress pipe:1 -nostats -loglevel error ").Append(Q(tempOut));

                RunFfmpegWithProgress(ffmpeg, a.ToString(), tempDir, info.Duration, 85, 100, sink);

                if (!File.Exists(tempOut) || new FileInfo(tempOut).Length < 10 * 1024)
                {
                    sink.Log("Encoding failed - keeping the raw download instead.");
                    File.Copy(tempAudio, target, true);
                }
                else
                {
                    File.Move(tempOut, target);
                }

                sink.Progress(100);
                sink.Log("Saved: " + target);
                return target;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // -----------------------------------------------------------------
        //  VIDEO
        // -----------------------------------------------------------------
        public static string DownloadVideo(string url, Config cfg, IJobSink sink)
        {
            string ytdlp = Tools.YtDlp, ffmpeg = Tools.FFmpeg;
            if (ytdlp == null) throw new FileNotFoundException("yt-dlp.exe not found. Check Settings > Tools.");
            if (ffmpeg == null) throw new FileNotFoundException("ffmpeg.exe not found. Check Settings > Tools.");

            string tempDir = Path.Combine(Path.GetTempPath(), "MediaPorter", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);

            try
            {
                sink.Stage("Reading video info");
                sink.Progress(-1);
                var info = ProbeTrack(ytdlp, url, sink);

                string name = Util.SafeFileName(Util.Clean(Util.StripJunk(info.Source)));
                if (string.IsNullOrWhiteSpace(name) || name == "untitled")
                    name = Util.SafeFileName(Util.Clean(info.Title));
                sink.Log("Title: " + name);

                // ---- download ------------------------------------------------
                sink.Stage("Downloading video");
                sink.Progress(0);

                string tempVideo = Path.Combine(tempDir, "raw.mp4");
                string dlArgs = YtFlags() +
                    "--no-playlist --extractor-args \"youtube:player_client=android,web\" --newline -N 8 -f \"bestvideo+bestaudio/best\" --merge-output-format mp4 " +
                    "--ffmpeg-location " + Q(Path.GetDirectoryName(ffmpeg)) +
                    " -o " + Q(tempVideo) + " " + Q(url);

                int code = Proc.Run(ytdlp, dlArgs, tempDir, line =>
                {
                    var m = RxDownloadPct.Match(line);
                    if (m.Success)
                        sink.Progress(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 0.45);
                    else if (line.Length > 0 && !line.StartsWith("[download] Destination"))
                        sink.Log(line);
                }, sink.Token);

                if (!File.Exists(tempVideo))
                {
                    // yt-dlp sometimes lands on a different container
                    var found = Directory.GetFiles(tempDir, "raw.*");
                    if (found.Length > 0) tempVideo = found[0];
                    else throw new Exception("yt-dlp could not download the video (exit " + code + ").");
                }

                // ---- transcode ------------------------------------------------
                DeviceProfile dev = cfg.Device;
                int w = dev.Width, h = dev.Height;
                sink.Stage("Transcoding for " + dev.Name + " (" + w + "x" + h + ")");
                sink.Log("Target: " + dev.Name + " - " + dev.Summary(cfg.PreferHevc));
                sink.Progress(45);

                string outDir = Path.Combine(cfg.VideoIncoming, AppPaths.Today);
                Directory.CreateDirectory(outDir);
                string target = Util.UniquePath(Path.Combine(outDir, name + ".mp4"));
                string tempOut = Path.Combine(tempDir, "out.mp4");

                // Fit inside the screen, keep the aspect ratio, centre what is left.
                // setsar=1 forces square pixels - without it ffmpeg preserves the source
                // display ratio with a lopsided pixel ratio, which some players stretch.
                string vf = "scale=" + w + ":" + h +
                            ":force_original_aspect_ratio=decrease:force_divisible_by=2," +
                            "pad=" + w + ":" + h + ":(ow-iw)/2:(oh-ih)/2,setsar=1";

                // Only touch the frame rate when the source is above what the iPod
                // takes - re-timing a 24 fps film to 30 just adds judder.
                if (info.Fps > dev.FpsCap + 0.5)
                {
                    vf += ",fps=" + dev.FpsCap;
                    sink.Log("Source is " + info.Fps.ToString("0.#", CultureInfo.InvariantCulture) +
                             " fps, capping to " + dev.FpsCap + " for this device.");
                }

                // Encoder ladder: the best GPU path first, a conservative GPU path for
                // older NVENC blocks (the GTX 1650's TU117 chip has no B-frame support),
                // then the CPU. Each rung is only tried if the one above it produced
                // nothing, so a machine without NVIDIA still ends up with a video.
                bool hevc = dev.UseHevc(cfg.PreferHevc);
                var attempts = new List<string[]>();

                if (cfg.UseGpu)
                {
                    string gen = Gpus.NvidiaGeneration();
                    sink.Log("Graphics: " + Gpus.Describe() + (gen.Length > 0 ? "  (" + gen + ")" : ""));

                    // Only queue an encoder if the vendor's hardware is actually present
                    // AND this ffmpeg was built with it. Two NVENC rungs because older
                    // blocks (the GTX 1650's TU117) reject B-frames and lookahead.
                    if (Gpus.HasNvidia && Tools.HasNvenc(ffmpeg, hevc))
                    {
                        attempts.Add(new[] { "NVIDIA GPU (NVENC)", GpuVideoArgs(cfg, dev, hevc, vf, name, tempVideo, tempOut, true) });
                        attempts.Add(new[] { "NVIDIA GPU (NVENC, basic)", GpuVideoArgs(cfg, dev, hevc, vf, name, tempVideo, tempOut, false) });
                    }
                    if (Gpus.HasAmd && Tools.HasAmf(ffmpeg, hevc))
                        attempts.Add(new[] { "AMD GPU (AMF)", AmdVideoArgs(dev, hevc, vf, name, tempVideo, tempOut) });
                    if (Gpus.HasIntel && Tools.HasQsv(ffmpeg, hevc))
                        attempts.Add(new[] { "Intel GPU (Quick Sync)", QsvVideoArgs(dev, hevc, vf, name, tempVideo, tempOut) });
                }

                attempts.Add(new[] { hevc ? "CPU (x265)" : "CPU (x264)",
                                     CpuVideoArgs(cfg, dev, hevc, vf, name, tempVideo, tempOut) });

                bool encoded = false;
                for (int i = 0; i < attempts.Count && !encoded; i++)
                {
                    Util.TryDelete(tempOut);
                    if (i > 0) sink.Log("Falling back to " + attempts[i][0] + "...");
                    else sink.Log("Encoding on the " + attempts[i][0] + ".");

                    RunFfmpegWithProgress(ffmpeg, attempts[i][1], tempDir, info.Duration, 45, 100, sink);
                    encoded = File.Exists(tempOut) && new FileInfo(tempOut).Length > 10 * 1024;
                }

                if (!encoded)
                    throw new Exception("ffmpeg could not transcode this video - see Details for what it reported.");

                File.Move(tempOut, target);
                sink.Progress(100);
                sink.Log("Saved: " + target);
                return target;
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>NVENC. "rich" turns on the quality extras that older NVENC blocks
        /// (Pascal, and the GTX 1650's TU117) reject outright.</summary>
        static string GpuVideoArgs(Config cfg, DeviceProfile dev, bool hevc, string vf,
                                   string title, string input, string output, bool rich)
        {
            var a = new StringBuilder();
            a.Append("-y -hwaccel auto -i ").Append(Q(input));
            a.Append(" -vf \"").Append(vf).Append("\"");
            if (hevc)
                a.Append(" -c:v hevc_nvenc -profile:v main -tag:v hvc1 -pix_fmt yuv420p");
            else
                a.Append(" -c:v h264_nvenc -profile:v ").Append(dev.Profile)
                 .Append(" -level ").Append(dev.Level).Append(" -pix_fmt yuv420p");
            a.Append(rich ? " -preset p7 -tune hq" : " -preset p5");
            a.Append(" -rc vbr -cq ").Append(dev.Cq).Append(" -b:v 0");
            a.Append(" -maxrate ").Append(dev.MaxRate)
             .Append(" -bufsize ").Append(ScaleRate(dev.MaxRate, 2.0));

            if (rich)
            {
                a.Append(" -spatial-aq 1 -aq-strength 8 -rc-lookahead 32");

                // How many B-frames this card and this profile will actually accept:
                //  - H.264 Baseline forbids B-slices outright, it is not a hardware limit.
                //  - H.264 B-frames work on every NVENC generation since Kepler.
                //  - HEVC B-frames need Turing or newer.
                int bf;
                if (hevc) bf = Gpus.NvencHevcBFrames ? 3 : 0;
                else if (dev.Profile.Equals("baseline", StringComparison.OrdinalIgnoreCase)) bf = 0;
                else bf = 3;
                a.Append(" -bf ").Append(bf);
            }
            a.Append(" -g 250");
            return a.Append(CommonVideoTail(dev, title, output)).ToString();
        }

        /// <summary>AMD AMF. Rate-controlled rather than quality-controlled on purpose:
        /// AMF's quality modes (qvbr and friends) vary by card generation, but vbr_peak
        /// is present on all of them and it is the one that actually honours the device's
        /// bitrate ceiling - which for something like the 5th-gen iPod is the difference
        /// between a file that plays and one that does not.
        /// NOT TESTED on real hardware - if it misbehaves the ladder drops to the CPU.</summary>
        static string AmdVideoArgs(DeviceProfile dev, bool hevc, string vf,
                                   string title, string input, string output)
        {
            var a = new StringBuilder();
            a.Append("-y -i ").Append(Q(input));
            a.Append(" -vf \"").Append(vf).Append("\"");

            int max = RateKbps(dev.MaxRate);
            int target = Math.Max(200, (int)(max * 0.6));

            if (hevc)
            {
                // AMF spells HEVC levels differently from H.264, so let it choose.
                a.Append(" -c:v hevc_amf -profile:v main -tag:v hvc1 -pix_fmt yuv420p");
            }
            else
            {
                a.Append(" -c:v h264_amf -pix_fmt yuv420p");
                a.Append(" -profile:v ").Append(AmfProfile(dev.Profile));
                string lvl = AmfLevel(dev.Level);
                if (lvl != null) a.Append(" -level ").Append(lvl);
            }

            a.Append(" -quality quality -rc vbr_peak");
            a.Append(" -b:v ").Append(target).Append("k");
            a.Append(" -maxrate ").Append(max).Append("k");
            a.Append(" -bufsize ").Append(max * 2).Append("k");
            return a.Append(CommonVideoTail(dev, title, output)).ToString();
        }

        /// <summary>AMF has no plain "baseline" - the equivalent is constrained_baseline.</summary>
        static string AmfProfile(string profile)
        {
            if (string.Equals(profile, "baseline", StringComparison.OrdinalIgnoreCase)) return "constrained_baseline";
            if (string.Equals(profile, "high", StringComparison.OrdinalIgnoreCase)) return "high";
            return "main";
        }

        /// <summary>AMF wants the level as an integer: 3.0 -> 30, 4.1 -> 41, 1.3 -> 13.</summary>
        static string AmfLevel(string level)
        {
            if (string.IsNullOrEmpty(level)) return null;
            string digits = level.Replace(".", "").Trim();
            int n;
            if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return null;
            return (n >= 0 && n <= 62) ? n.ToString(CultureInfo.InvariantCulture) : null;
        }

        static int RateKbps(string rate)
        {
            var m = Regex.Match(rate ?? "", @"(\d+)");
            if (!m.Success) return 1400;
            int v = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if ((rate ?? "").IndexOf('M') >= 0 || (rate ?? "").IndexOf('m') >= 0) v *= 1000;
            return v;
        }

        /// <summary>Intel Quick Sync. global_quality is Intel's CRF equivalent, so this
        /// one can stay quality-targeted like the NVIDIA and CPU paths.
        /// NOT TESTED on real hardware - the ladder drops to the CPU if it fails.</summary>
        static string QsvVideoArgs(DeviceProfile dev, bool hevc, string vf,
                                   string title, string input, string output)
        {
            var a = new StringBuilder();
            a.Append("-y -i ").Append(Q(input));
            a.Append(" -vf \"").Append(vf).Append("\"");

            if (hevc)
            {
                a.Append(" -c:v hevc_qsv -profile:v main -tag:v hvc1");
            }
            else
            {
                a.Append(" -c:v h264_qsv -profile:v ").Append(dev.Profile);
                string lvl = AmfLevel(dev.Level);      // QSV wants the same integer form
                if (lvl != null) a.Append(" -level ").Append(lvl);
            }

            a.Append(" -preset veryslow -global_quality ").Append(dev.Cq);
            a.Append(" -maxrate ").Append(dev.MaxRate)
             .Append(" -bufsize ").Append(ScaleRate(dev.MaxRate, 2.0));
            a.Append(" -pix_fmt nv12");
            return a.Append(CommonVideoTail(dev, title, output)).ToString();
        }

        static string CpuVideoArgs(Config cfg, DeviceProfile dev, bool hevc, string vf,
                                   string title, string input, string output)
        {
            var a = new StringBuilder();
            a.Append("-y -i ").Append(Q(input));
            a.Append(" -vf \"").Append(vf).Append("\"");
            if (hevc)
                a.Append(" -c:v libx265 -preset medium -profile:v main -tag:v hvc1 -pix_fmt yuv420p");
            else
                a.Append(" -c:v libx264 -preset slow -profile:v ").Append(dev.Profile)
                 .Append(" -level:v ").Append(dev.Level).Append(" -pix_fmt yuv420p");
            a.Append(" -crf ").Append(dev.Crf);
            a.Append(" -maxrate ").Append(dev.MaxRate)
             .Append(" -bufsize ").Append(ScaleRate(dev.MaxRate, 2.0));
            return a.Append(CommonVideoTail(dev, title, output)).ToString();
        }

        static string CommonVideoTail(DeviceProfile dev, string title, string output)
        {
            return " -c:a aac -b:a " + dev.AudioBitrate + " -ar 44100 -ac 2" +
                   " -movflags +faststart -metadata title=" + Q(title) +
                   " -progress pipe:1 -nostats -loglevel error " + Q(output);
        }

        // -----------------------------------------------------------------
        //  Batch loudness normaliser (2-pass EBU R128) for files already on disk
        // -----------------------------------------------------------------
        public static void NormalizeFile(string file, Config cfg, IJobSink sink)
        {
            string ffmpeg = Tools.FFmpeg;
            if (ffmpeg == null) throw new FileNotFoundException("ffmpeg.exe not found.");

            string temp = Path.Combine(Path.GetTempPath(), "MediaPorter_norm_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".m4a");

            // Pass 1 - measure
            sink.Stage("Analysing " + Path.GetFileName(file));
            var json = new StringBuilder();
            bool capture = false;
            string measureArgs = "-hide_banner -i " + Q(file) + " -af loudnorm=I=" + N(cfg.Loudness) +
                                 ":TP=" + N(cfg.TruePeak) + ":LRA=11:print_format=json -f null -";
            Proc.Run(ffmpeg, measureArgs, null, line =>
            {
                if (line.Trim().StartsWith("{")) capture = true;
                if (capture) json.AppendLine(line);
                if (line.Trim().StartsWith("}")) capture = false;
            }, sink.Token);

            object measured = null;
            try { measured = Json.Parse(json.ToString()); } catch { }

            var filter = new StringBuilder("loudnorm=I=" + N(cfg.Loudness) + ":TP=" + N(cfg.TruePeak) + ":LRA=11");
            if (measured != null && !string.IsNullOrEmpty(Json.Str(measured, "input_i")))
            {
                filter.Append(":measured_I=").Append(Json.Str(measured, "input_i"));
                filter.Append(":measured_TP=").Append(Json.Str(measured, "input_tp"));
                filter.Append(":measured_LRA=").Append(Json.Str(measured, "input_lra"));
                filter.Append(":measured_thresh=").Append(Json.Str(measured, "input_thresh"));
                filter.Append(":offset=").Append(Json.Str(measured, "target_offset"));
                filter.Append(":linear=true");
            }

            // Pass 2 - apply, keeping artwork + tags
            sink.Stage("Normalising " + Path.GetFileName(file));
            string applyArgs = "-y -i " + Q(file) + " -map 0 -map_metadata 0 -c copy -c:a aac -b:a " + cfg.AudioBitrate +
                               " -ar 44100 -filter:a \"" + filter + "\" -progress pipe:1 -nostats -loglevel error " + Q(temp);

            RunFfmpegWithProgress(ffmpeg, applyArgs, null, 0, 0, 100, sink);

            if (File.Exists(temp) && new FileInfo(temp).Length > 10 * 1024)
            {
                File.Copy(temp, Path.ChangeExtension(file, ".m4a"), true);
                if (!string.Equals(Path.GetExtension(file), ".m4a", StringComparison.OrdinalIgnoreCase))
                    Util.TryDelete(file);
                Util.TryDelete(temp);
                sink.Log("Normalised: " + Path.GetFileName(file));
            }
            else
            {
                Util.TryDelete(temp);
                throw new Exception("Normalisation failed for " + Path.GetFileName(file));
            }
        }

        // -----------------------------------------------------------------
        //  helpers
        // -----------------------------------------------------------------
        static string Fallback(string s, string f) { return string.IsNullOrWhiteSpace(s) ? f : s; }
        static string N(double d) { return d.ToString("0.##", CultureInfo.InvariantCulture); }

        static string ScaleRate(string rate, double factor)
        {
            var m = Regex.Match(rate ?? "", @"(\d+)\s*([kKmM]?)");
            if (!m.Success) return rate;
            double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * factor;
            return ((int)Math.Round(v)) + m.Groups[2].Value.ToLowerInvariant();
        }

        static void RunFfmpegWithProgress(string ffmpeg, string args, string workDir,
                                          double durationSec, double from, double to, IJobSink sink)
        {
            bool haveDuration = durationSec > 0.5;
            if (!haveDuration) sink.Progress(-1);

            Proc.Run(ffmpeg, args, workDir, line =>
            {
                if (haveDuration)
                {
                    double seconds = -1;
                    var m = RxOutTime.Match(line);
                    if (m.Success) seconds = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1000000.0;
                    else
                    {
                        var m2 = RxOutTimeMs.Match(line);
                        if (m2.Success) seconds = double.Parse(m2.Groups[1].Value, CultureInfo.InvariantCulture) / 1000000.0;
                    }
                    if (seconds >= 0)
                    {
                        double pct = Math.Max(0, Math.Min(1, seconds / durationSec));
                        sink.Progress(from + (to - from) * pct);
                        return;
                    }
                }
                if (line.StartsWith("progress=") || line.Contains("=") && line.IndexOf(' ') < 0) return;
                if (line.Trim().Length > 0) sink.Log(line);
            }, sink.Token);
        }

        public static TrackInfo ProbeTrack(string ytdlp, string url, IJobSink sink)
        {
            var info = new TrackInfo();
            string sep = ";;;";
            string args = YtFlags() + "--no-playlist --extractor-args \"youtube:player_client=android,web\" " +
                          "--print \"%(title)s" + sep + "%(artist)s" + sep + "%(track)s" + sep +
                          "%(uploader)s" + sep + "%(duration)s" + sep + "%(fps)s\" " + Q(url);

            string raw = "";
            Proc.Run(ytdlp, args, null, line =>
            {
                if (line.Contains(sep) && raw.Length == 0) raw = line;
                else if (line.StartsWith("ERROR")) sink.Log(line);
            }, sink.Token);

            if (string.IsNullOrWhiteSpace(raw))
                throw new Exception("Could not read video info. The link may be private, region locked, or yt-dlp needs updating (Settings > Update tools).");

            string[] p = Regex.Split(raw, sep);
            string rawTitle = Field(p, 0, "downloaded_track");
            string ytArtist = Field(p, 1, "");
            string ytTrack = Field(p, 2, "");
            string uploader = Field(p, 3, "");
            double dur;
            if (p.Length > 4 && double.TryParse(Field(p, 4, "0"), NumberStyles.Any, CultureInfo.InvariantCulture, out dur))
                info.Duration = dur;
            double fps;
            if (p.Length > 5 && double.TryParse(Field(p, 5, "0"), NumberStyles.Any, CultureInfo.InvariantCulture, out fps))
                info.Fps = fps;

            info.Source = rawTitle;                       // untouched title, used for video filenames
            string clean = Util.StripJunk(rawTitle);

            var dash = Regex.Split(clean, @"\s+-\s+");
            if (dash.Length >= 2)
            {
                info.Artist = dash[0].Trim();
                info.Title = string.Join(" - ", dash, 1, dash.Length - 1).Trim();
            }
            else
            {
                info.Title = clean.Trim();
                if (!string.IsNullOrWhiteSpace(ytArtist))
                {
                    info.Artist = ytArtist;
                    if (!string.IsNullOrWhiteSpace(ytTrack)) info.Title = ytTrack;
                }
                else if (!string.IsNullOrWhiteSpace(uploader))
                {
                    info.Artist = Regex.Replace(uploader,
                        @"\s*(Music|Official|VEVO|- Topic|Records|Audio|Video|Channel)\s*$", "",
                        RegexOptions.IgnoreCase).Trim();
                }
            }

            info.Artist = Fallback(Util.Clean(info.Artist), "Unknown Artist");
            info.Title = Fallback(Util.Clean(info.Title), "downloaded_track");
            return info;
        }

        static string Field(string[] parts, int i, string fallback)
        {
            if (parts == null || i >= parts.Length) return fallback;
            string s = (parts[i] ?? "").Trim();
            if (s.Length == 0 || s == "NA" || s == "None") return fallback;
            return s;
        }

        /// <summary>iTunes Search API first, MusicBrainz + Cover Art Archive as fallback.</summary>
        static void Enrich(TrackInfo info, IJobSink sink)
        {
            string query = info.Artist == "Unknown Artist" ? info.Title : info.Artist + " " + info.Title;

            // --- Apple iTunes ---
            try
            {
                string u = "https://itunes.apple.com/search?term=" + Uri.EscapeDataString(query) + "&entity=song&limit=1";
                object j = Json.Parse(Util.Http(u, 6000));
                if (Json.Num(j, "resultCount") > 0)
                {
                    var r = Json.Arr(j, "results");
                    if (r.Count > 0)
                    {
                        object t = r[0];
                        string title = Json.Str(t, "trackName");
                        string artist = Json.Str(t, "artistName");
                        if (!string.IsNullOrWhiteSpace(title)) info.Title = title;
                        if (!string.IsNullOrWhiteSpace(artist)) info.Artist = artist;
                        string album = Json.Str(t, "collectionName");
                        string genre = Json.Str(t, "primaryGenreName");
                        if (!string.IsNullOrWhiteSpace(album)) info.Album = album;
                        if (!string.IsNullOrWhiteSpace(genre)) info.Genre = genre;
                        string art = Json.Str(t, "artworkUrl100");
                        if (!string.IsNullOrWhiteSpace(art))
                            info.ArtworkUrl = art.Replace("100x100bb", "1000x1000bb");
                        sink.Log("Matched on iTunes: " + info.Artist + " - " + info.Title + " (" + info.Album + ")");
                        return;
                    }
                }
            }
            catch (Exception ex) { sink.Log("iTunes lookup skipped: " + ex.Message); }

            // --- MusicBrainz + Cover Art Archive ---
            try
            {
                string q = info.Artist == "Unknown Artist"
                    ? "recording:\"" + info.Title + "\""
                    : "artist:\"" + info.Artist + "\" AND recording:\"" + info.Title + "\"";
                string u = "https://musicbrainz.org/ws/2/recording/?query=" + Uri.EscapeDataString(q) + "&fmt=json&limit=1";
                object j = Json.Parse(Util.Http(u, 6000, "application/json"));
                var recs = Json.Arr(j, "recordings");
                if (recs.Count == 0) { sink.Log("No official match found - keeping YouTube tags."); return; }

                object rec = recs[0];
                string title = Json.Str(rec, "title");
                if (!string.IsNullOrWhiteSpace(title)) info.Title = title;
                string ac = Json.Str(rec, "artist-credit[0].artist.name");
                if (!string.IsNullOrWhiteSpace(ac)) info.Artist = ac;

                string relId = Json.Str(rec, "releases[0].id");
                string album = Json.Str(rec, "releases[0].title");
                string rgId = Json.Str(rec, "releases[0].release-group.id");
                if (!string.IsNullOrWhiteSpace(album)) info.Album = album;
                string tag = Json.Str(rec, "tags[0].name");
                if (!string.IsNullOrWhiteSpace(tag)) info.Genre = tag;

                foreach (string caa in new[] {
                    string.IsNullOrEmpty(relId) ? null : "https://coverartarchive.org/release/" + relId,
                    string.IsNullOrEmpty(rgId) ? null : "https://coverartarchive.org/release-group/" + rgId })
                {
                    if (caa == null) continue;
                    try
                    {
                        object cj = Json.Parse(Util.Http(caa, 5000, "application/json"));
                        string img = Json.Str(cj, "images[0].image");
                        if (!string.IsNullOrWhiteSpace(img)) { info.ArtworkUrl = img; break; }
                    }
                    catch { }
                }
                sink.Log("Matched on MusicBrainz: " + info.Artist + " - " + info.Title);
            }
            catch
            {
                sink.Log("No official match found - keeping YouTube tags.");
            }
        }

        /// <summary>Returns a 1:1 square jpg for embedding, or null.</summary>
        static string BuildCover(string ffmpeg, string tempDir, TrackInfo info, IJobSink sink)
        {
            string square = Path.Combine(tempDir, "cover.jpg");

            if (!string.IsNullOrEmpty(info.ArtworkUrl))
            {
                try
                {
                    string dl = Path.Combine(tempDir, "art_src.jpg");
                    Util.Download(info.ArtworkUrl, dl);
                    Proc.Run(ffmpeg, "-y -i " + Q(dl) + " -vf \"crop='min(iw,ih)':'min(iw,ih)'\" -loglevel error " + Q(square), tempDir, l => { }, sink.Token);
                    if (File.Exists(square)) { sink.Log("Embedded official album art."); return square; }
                }
                catch (Exception ex) { sink.Log("Album art download failed (" + ex.Message + ") - using the video thumbnail."); }
            }

            foreach (string ext in new[] { "*.jpg", "*.webp", "*.png" })
            {
                var files = Directory.GetFiles(tempDir, ext);
                foreach (string f in files)
                {
                    if (Path.GetFileName(f).StartsWith("cover") || Path.GetFileName(f).StartsWith("art_src")) continue;
                    try
                    {
                        Proc.Run(ffmpeg, "-y -i " + Q(f) + " -vf \"crop='min(iw,ih)':'min(iw,ih)'\" -loglevel error " + Q(square), tempDir, l => { }, sink.Token);
                        if (File.Exists(square)) { sink.Log("Embedded the video thumbnail, cropped square."); return square; }
                    }
                    catch { }
                }
            }
            return null;
        }
    }
}
