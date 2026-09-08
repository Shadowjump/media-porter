# Media Porter

Get music and video onto classic iPods, iPhones and iPads — without opening iTunes.

Paste a YouTube link, pick your device, press Download. You get a file encoded to
exactly what that device's screen and decoder can take, tagged, volume-matched,
and copied onto the device. A single window, no installer, no configuration.

> Media Porter drives iTunes through its COM automation API, so **iTunes must be
> installed** for the transfer step. You just never have to look at it. Downloading
> and converting work without it entirely.

---

## What it does

| | |
|---|---|
| **Music** | Fetches the audio, looks the track up on the iTunes Search API and MusicBrainz for real artist/title/album/genre, pulls the official cover art cropped square, normalises to −16 LUFS (EBU R128, matching Apple Sound Check) and writes a 192 kbps AAC `.m4a`. |
| **Video** | Downloads the best available source and transcodes it to the exact screen size, frame-rate ceiling, H.264 profile and level your chosen device is documented to play. Hardware-encoded on whatever GPU you have. |
| **Send to device** | Copies everything pending onto the device, then archives it into a dated folder. |
| **Library** | Browse what's on the device itself — filter, select, delete — plus what's waiting to be sent. |

Paste several links at once, one per line; they queue and run in order.

---

## Why not just encode everything at 1080p?

Because these devices throw most of it away, and the differences between them are
not what you'd guess. All of this comes from Apple's own published specs:

- The **iPod nano 7th gen** has a 240 × 432 screen but accepts H.264 **High**
  profile — better than the Baseline most guides recommend.
- The **iPod touch 5th gen** is documented as **Main** profile, not High.
- The **iPad 2** decodes 1080p High 4.1 onto a **1024 × 768** panel — four times
  more pixels than it can display.
- The **iPod 5th gen** (2005) is capped at Baseline **level 1.3** and 768 kbps.
  Hand it anything richer and it simply refuses to play.
- The **iPod nano 6th gen** plays no video at all.

So each of the 22 video-capable devices carries its own resolution, fps cap,
profile, level and bitrate ceiling. Encoding to the screen rather than to the
decoder typically cuts file size by 3–4× with no visible difference on the device.

The whole table lives in [`ui/devices.json`](ui/devices.json) and is read at
startup — correct a device or add one, restart, done. No rebuild. Entries taken
straight from an Apple spec page are marked `verified`; the rest are derived from
the verified model of the same generation, always erring low. A slightly softer
picture costs nothing; a file above what the decoder accepts won't play at all.

---

## Encoding

Video is **quality-targeted**, not fixed-bitrate — a static shot stays small, a
busy music video gets the bits it needs, both capped at what the device allows.

**Aspect ratio is never altered.** Scaled to fit, centred, square pixels forced.
**Frame rate is preserved** — 24 stays 24, 25 stays 25 — and only capped when the
source exceeds what the device supports.

The GPU is used when there is one. Windows is asked which adapters are actually
present, then only those vendors' encoders are tried, in order, stopping at the
first that produces a file:

1. **NVIDIA** — NVENC (two passes: full quality, then conservative)
2. **AMD** — AMF
3. **Intel** — Quick Sync
4. **CPU** — x264 / x265

Settings are matched to what the specific silicon supports. H.264 B-frames are
enabled everywhere except where the *format* forbids them (Baseline has no
B-slices, so old-iPod profiles get `-bf 0`); HEVC B-frames require Turing or
newer; the AMD path never forces B-frames, because AMD dropped them with the VCN
engine and only restored them in RDNA 2.

**Tested on:** NVIDIA (RTX 50-series) and AMD (Radeon iGPU), verified down to the
profile and level bytes in the output bitstream. **Intel Quick Sync is written
from documentation and has not been run on real hardware** — if it misbehaves the
ladder falls through to the CPU, so the worst case is a slower encode.

---

## Getting started

**Requirements:** Windows 10 or 11. That's it to download and convert — .NET
Framework 4.8 is already part of Windows. iTunes is needed only to copy onto a
device.

1. Clone or download this repo.
2. Double-click **`source\build.bat`**. It compiles with the C# compiler that
   ships inside Windows, so there is nothing to install — no SDK, no Visual
   Studio, no internet. `MediaPorter.exe` appears in the repo root.
3. Run it. On first launch open **Settings → Copy tools into this folder** to
   fetch `yt-dlp` and `ffmpeg` (they are not committed here — ffmpeg alone is
   ~97 MB, over GitHub's per-file limit).
4. Pick your device from the chip on the Video page.

---

## Portable, and self-updating

Everything lives in one folder. Copy it to a USB stick or another PC and it runs:

```
MediaPorter.exe          the app
ui/                      the entire interface, as loose XAML - edit and restart,
                         no rebuild needed
ui/devices.json          the device table
bin/                     yt-dlp.exe, ffmpeg.exe   (fetched on first run)
data/config.json         your settings            (written on first run)
source/                  C# source + build.bat
```

- **yt-dlp** is checked against GitHub once a day and offered when a newer release
  exists. This is the fix whenever YouTube changes something and downloads break.
- **ffmpeg** updates from the FFmpeg-Builds releases on demand.
- **The app itself** can rebuild from its own source and restart into the new
  build (Settings → Rebuild app). If the build fails, nothing is replaced.

Nothing is ever written outside the app folder and your chosen media folders.

---

## Third-party tools

Media Porter downloads these at runtime; neither is bundled or redistributed here:

- **[yt-dlp](https://github.com/yt-dlp/yt-dlp)** — Unlicense
- **[FFmpeg](https://ffmpeg.org/)** — builds from
  [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), GPL

---

## Notes

Not affiliated with, endorsed by, or connected to Apple Inc. iPod, iPhone, iPad
and iTunes are trademarks of Apple Inc., used here only to describe which devices
this tool works with.

For personal use with content you have the right to download. Respect copyright
and the terms of the sites you fetch from.

Licensed under the [MIT License](LICENSE).
