using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MediaPorter
{
    /// <summary>One Apple device and the video it is willing to play.</summary>
    public class DeviceProfile
    {
        public string Id = "";
        public string Name = "";
        public string Family = "";
        public string Years = "";
        public string Screen = "";
        public int Width = 432;
        public int Height = 240;
        public int FpsCap = 30;
        public string Codec = "h264";
        public string Profile = "high";
        public string Level = "3.0";
        public string MaxRate = "1400k";
        public int Crf = 20;
        public int Cq = 23;
        public string AudioBitrate = "160k";
        public bool Hevc = false;
        public bool Verified = false;
        /// <summary>False for devices that play music but have no video at all.</summary>
        public bool Video = true;
        public string Note = "";

        public string Resolution
        {
            get { return Width + " x " + Height; }
        }

        /// <summary>What the encode will actually be, once HEVC preference is applied.</summary>
        public bool UseHevc(bool preferHevc)
        {
            return preferHevc && Hevc;
        }

        public string Summary(bool preferHevc)
        {
            string codec = UseHevc(preferHevc)
                ? "HEVC"
                : "H.264 " + Cap(Profile) + " " + Level;
            return Resolution + " · " + codec + " · AAC " + AudioBitrate;
        }

        static string Cap(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0]) + s.Substring(1);
        }
    }

    /// <summary>Loads ui\devices.json. Falls back to a built-in nano 7G profile so the
    /// app still works if the file is missing or someone breaks the JSON.</summary>
    public static class Devices
    {
        static List<DeviceProfile> _all;

        public static string FilePath
        {
            get { return Path.Combine(AppPaths.UiDir, "devices.json"); }
        }

        public static List<DeviceProfile> All
        {
            get
            {
                if (_all == null) _all = Load();
                return _all;
            }
        }

        public static void Reload() { _all = null; }

        public static DeviceProfile Find(string id)
        {
            foreach (DeviceProfile d in All)
                if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) return d;

            // Never fall back onto a music-only entry - it carries no encode settings.
            foreach (DeviceProfile d in All)
                if (d.Video) return d;
            return Fallback();
        }

        static DeviceProfile Fallback()
        {
            return new DeviceProfile
            {
                Id = "ipod-nano-7g",
                Name = "iPod nano (7th gen)",
                Family = "iPod nano",
                Screen = "240 x 432",
                Width = 432,
                Height = 240,
                Verified = true,
                Note = "Built-in fallback - ui\\devices.json could not be read."
            };
        }

        static List<DeviceProfile> Load()
        {
            var list = new List<DeviceProfile>();
            try
            {
                object root = Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                foreach (object item in Json.Arr(root, "devices"))
                {
                    var d = new DeviceProfile();
                    d.Id = Json.Str(item, "id");
                    d.Name = Json.Str(item, "name");
                    d.Family = Json.Str(item, "family");
                    d.Years = Json.Str(item, "years");
                    d.Screen = Json.Str(item, "screen");
                    d.Width = (int)Json.Num(item, "width", d.Width);
                    d.Height = (int)Json.Num(item, "height", d.Height);
                    d.FpsCap = (int)Json.Num(item, "fpsCap", d.FpsCap);
                    d.Codec = Json.Str(item, "codec", d.Codec);
                    d.Profile = Json.Str(item, "profile", d.Profile);
                    d.Level = Json.Str(item, "level", d.Level);
                    d.MaxRate = Json.Str(item, "maxRate", d.MaxRate);
                    d.Crf = (int)Json.Num(item, "crf", d.Crf);
                    d.Cq = (int)Json.Num(item, "cq", d.Cq);
                    d.AudioBitrate = Json.Str(item, "audioBitrate", d.AudioBitrate);
                    d.Hevc = Json.Bool(item, "hevc", false);
                    d.Video = Json.Bool(item, "video", true);
                    d.Verified = Json.Bool(item, "verified", false);
                    d.Note = Json.Str(item, "note");
                    if (!string.IsNullOrEmpty(d.Id)) list.Add(d);
                }
            }
            catch { }

            if (list.Count == 0) list.Add(Fallback());
            return list;
        }
    }
}
