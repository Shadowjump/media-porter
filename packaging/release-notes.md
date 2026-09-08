Get music and video onto classic iPods, iPhones and iPads without opening iTunes.

Paste a YouTube link, pick your device, press Download. You get a file encoded to
exactly what that device's screen and decoder can take — tagged, volume-matched,
and copied onto the device.

## Which file do I want?

| | |
|---|---|
| **MediaPorter-1.0.0-setup.exe** | Normal install. Goes to `C:\Program Files\Media Porter`, asks for administrator permission once, adds a Start Menu entry and an uninstaller. |
| **MediaPorter-1.0.0-portable.zip** | Unzip and run. Everything stays in that one folder, including settings and tools. Good for a USB stick. |

**Requirements:** Windows 10 or 11. Nothing else to install — .NET Framework 4.8
is already part of Windows. iTunes is only needed to copy files onto a device;
downloading and converting work without it.

On first launch it offers to fetch **yt-dlp** and **ffmpeg** with a progress bar
for each — one click, about 115 MB, straight from their own GitHub releases.
They are not bundled here.

> Windows will show a **SmartScreen warning** the first time ("Windows protected
> your PC"). That is what an unsigned download from a small project looks like —
> click *More info → Run anyway*, or use the portable zip. Removing that warning
> needs a paid code-signing certificate.

## Why it exists

These devices throw away most of what you'd normally hand them, and the
differences between them are not what you'd guess:

- The **iPod nano 7th gen** has a 240 × 432 screen but accepts H.264 **High**
  profile — better than the Baseline most guides recommend.
- The **iPod touch 5th gen** is documented as **Main** profile, not High.
- The **iPad 2** decodes 1080p onto a **1024 × 768** panel — four times more
  pixels than it can show.
- The **iPod 5th gen** (2005) is capped at Baseline **level 1.3** and 768 kbps.
  Anything richer simply refuses to play.
- The **iPod nano 6th gen** plays no video at all.

So each of the 22 video-capable devices carries its own resolution, frame-rate
cap, profile, level and bitrate ceiling. Encoding to the screen rather than to
the decoder typically cuts file size 3–4× with no visible difference.

Aspect ratio is never altered and the source frame rate is preserved — 24 stays
24 — capped only where the device requires it.

## Encoding

Hardware encoding on whatever GPU is present, chosen by asking Windows what is
actually installed rather than guessing: NVIDIA (NVENC) → AMD (AMF) → Intel
(Quick Sync) → CPU, stopping at the first that works. Settings are matched to
what that specific silicon supports, down to which generations allow B-frames.

Verified on NVIDIA and AMD hardware, checked to the profile and level bytes in
the output bitstream. **Intel Quick Sync is written from documentation and has
not been run on real hardware** — if it misbehaves it falls through to the CPU,
so the worst case is a slower encode.

## Verify your download

```
<!--CHECKSUMS-->
```

Not affiliated with Apple Inc. MIT licensed.
