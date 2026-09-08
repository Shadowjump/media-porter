using System;
using System.Collections.Generic;
using System.Management;

namespace MediaPorter
{
    /// <summary>What graphics hardware is actually in this PC.
    ///
    /// ffmpeg listing h264_qsv only means it was *built* with Quick Sync support, not
    /// that there is an Intel chip to run it on. Asking Windows what adapters exist
    /// lets the encoder ladder start with the one most likely to work instead of
    /// burning a failed attempt on hardware that is not there.</summary>
    public static class Gpus
    {
        static bool _probed;
        static readonly List<string> _names = new List<string>();
        static bool _nvidia, _amd, _intel;

        public static List<string> Names { get { Probe(); return _names; } }
        public static bool HasNvidia { get { Probe(); return _nvidia; } }
        public static bool HasAmd { get { Probe(); return _amd; } }
        public static bool HasIntel { get { Probe(); return _intel; } }

        public static bool Any { get { return HasNvidia || HasAmd || HasIntel; } }

        /// <summary>Can this NVIDIA card do B-frames in HEVC?
        ///
        /// H.264 B-frames have worked on every NVENC generation since Kepler, so they
        /// are never the problem. HEVC B-frames only arrived with Turing. The awkward
        /// case is the GTX 1650: it launched with TU117 carrying the older Volta NVENC
        /// (no HEVC B-frames) and was later revised to the Turing engine. The name does
        /// not say which one you have, so a plain "GTX 1650" is treated as the older
        /// part - encoding a little less efficiently costs nothing, while asking for a
        /// feature the silicon lacks fails the whole encode.</summary>
        public static bool NvencHevcBFrames
        {
            get
            {
                Probe();
                if (!_nvidia) return false;
                foreach (string name in _names)
                {
                    string n = name.ToLowerInvariant();
                    if (n.IndexOf("nvidia") < 0 && n.IndexOf("geforce") < 0 &&
                        n.IndexOf("rtx") < 0 && n.IndexOf("gtx") < 0 && n.IndexOf("quadro") < 0)
                        continue;

                    // The one card we deliberately treat as pre-Turing.
                    if (n.Contains("1650") && !n.Contains("super")) return false;

                    // Turing and later: RTX anything, and the GTX 16-series.
                    if (n.Contains("rtx")) return true;
                    if (n.Contains("gtx 16") || n.Contains("gtx16")) return true;
                }
                return false;
            }
        }

        /// <summary>Rough generation label, for the log and the tools list.</summary>
        public static string NvidiaGeneration()
        {
            Probe();
            foreach (string name in _names)
            {
                string n = name.ToLowerInvariant();
                if (n.Contains("rtx 50") || n.Contains("rtx50")) return "Blackwell";
                if (n.Contains("rtx 40") || n.Contains("rtx40")) return "Ada";
                if (n.Contains("rtx 30") || n.Contains("rtx30")) return "Ampere";
                if (n.Contains("rtx 20") || n.Contains("rtx20")) return "Turing";
                if (n.Contains("1650") && !n.Contains("super")) return "Turing (Volta encoder)";
                if (n.Contains("gtx 16") || n.Contains("gtx16")) return "Turing";
                if (n.Contains("gtx 10") || n.Contains("gtx10")) return "Pascal";
            }
            return "";
        }

        public static string Describe()
        {
            Probe();
            if (_names.Count == 0) return "no graphics adapter reported";
            return string.Join(", ", _names.ToArray());
        }

        public static void Probe()
        {
            if (_probed) return;
            _probed = true;

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_VideoController"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject mo in results)
                    {
                        object n = mo["Name"];
                        if (n == null) continue;
                        string name = n.ToString().Trim();
                        if (name.Length == 0) continue;
                        _names.Add(name);

                        string lower = name.ToLowerInvariant();
                        if (lower.Contains("nvidia") || lower.Contains("geforce") ||
                            lower.Contains("quadro") || lower.Contains("rtx") || lower.Contains("gtx"))
                            _nvidia = true;
                        else if (lower.Contains("amd") || lower.Contains("radeon") || lower.Contains("firepro"))
                            _amd = true;
                        else if (lower.Contains("intel") || lower.Contains("arc") ||
                                 lower.Contains("iris") || lower.Contains("uhd graphics") ||
                                 lower.Contains("hd graphics"))
                            _intel = true;
                    }
                }
            }
            catch
            {
                // WMI can be disabled or broken. Assume nothing and let the encoder
                // ladder try everything ffmpeg offers, exactly as it did before.
                _nvidia = _amd = _intel = true;
            }

            // Nothing recognised: do not lock ourselves out of the GPU encoders.
            if (!_nvidia && !_amd && !_intel && _names.Count == 0)
                _nvidia = _amd = _intel = true;
        }
    }
}
