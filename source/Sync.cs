using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Threading;

namespace MediaPorter
{
    /// <summary>Late-bound COM helpers - no interop assembly needed, so the app
    /// compiles anywhere and runs with whatever iTunes version is installed.</summary>
    static class Com
    {
        public static object Prop(object o, string name, params object[] args)
        {
            return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, args);
        }

        public static object Call(object o, string name, params object[] args)
        {
            return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
        }

        public static int Int(object o, string name, params object[] args)
        {
            object v = Prop(o, name, args);
            return v == null ? 0 : Convert.ToInt32(v);
        }

        public static string Text(object o, string name, params object[] args)
        {
            object v = Prop(o, name, args);
            return v == null ? "" : v.ToString();
        }

        public static bool Flag(object o, string name)
        {
            try { object v = Prop(o, name); return v != null && Convert.ToBoolean(v); }
            catch { return false; }
        }

        public static void Release(object o)
        {
            try { if (o != null && System.Runtime.InteropServices.Marshal.IsComObject(o))
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(o); }
            catch { }
        }
    }

    public class DeviceStatus
    {
        public bool ITunesRunning;
        public bool Connected;
        public string DeviceName = "";
        public int TrackCount;
        public string Message = "";

        /// <summary>True when the real problem is this process running elevated, not
        /// iTunes actually being missing - see IsElevated().</summary>
        public bool BlockedByElevation;
    }

    public static class ITunesSync
    {
        const int SourceKindIPod = 2;
        const int PlaylistKindLibrary = 1;

        public static bool ITunesInstalled()
        {
            return Type.GetTypeFromProgID("iTunes.Application") != null;
        }

        /// <summary>True only when this process itself is running elevated (not merely
        /// "the user is an admin" - UAC gives an admin account a filtered, non-elevated
        /// token by default, and only a real "Run as administrator" launch flips this).
        /// The Microsoft Store build of iTunes registers its COM automation per-user;
        /// an elevated caller runs in a different security context and cannot reach it,
        /// which otherwise looks exactly like "iTunes is not installed".</summary>
        public static bool IsElevated()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>Shared wording for the "!ITunesInstalled()" case, used everywhere that
        /// check gates a COM call - correctly blames elevation instead of a missing iTunes
        /// when that is the real cause.</summary>
        static string NotInstalledMessage()
        {
            return IsElevated()
                ? "This app is running as Administrator, which blocks it from reaching the Microsoft Store version of iTunes. Close it and reopen it normally - it never needs elevation."
                : "iTunes is not installed on this PC.";
        }

        public static bool ITunesRunning()
        {
            try { return Process.GetProcessesByName("iTunes").Length > 0; }
            catch { return false; }
        }

        public static void LaunchITunes()
        {
            try
            {
                Process.Start(new ProcessStartInfo("iTunes.exe") { UseShellExecute = true });
            }
            catch
            {
                foreach (string p in new[]
                {
                    @"C:\Program Files\iTunes\iTunes.exe",
                    @"C:\Program Files (x86)\iTunes\iTunes.exe"
                })
                {
                    if (File.Exists(p))
                    {
                        try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); return; } catch { }
                    }
                }
            }
        }

        /// <summary>Looks for a connected iPod. Only touches COM when iTunes is already running.</summary>
        public static DeviceStatus Probe(bool allowLaunch)
        {
            var st = new DeviceStatus();
            if (!ITunesInstalled())
            {
                if (IsElevated())
                {
                    st.BlockedByElevation = true;
                    st.ITunesRunning = ITunesRunning();
                    st.Message = st.ITunesRunning
                        ? "iTunes is running, but this app can't reach it while running as Administrator - the Microsoft Store version of iTunes is walled off from elevated processes. Close this app and reopen it normally (not \"Run as administrator\")."
                        : "This app is running as Administrator, which blocks it from reaching the Microsoft Store version of iTunes. Close it and reopen it normally - it never needs elevation.";
                }
                else
                {
                    st.Message = "iTunes is not installed on this PC.";
                }
                return st;
            }

            st.ITunesRunning = ITunesRunning();
            if (!st.ITunesRunning && !allowLaunch)
            {
                st.Message = "iTunes is not running.";
                return st;
            }

            object itunes = null, sources = null;
            try
            {
                itunes = Activator.CreateInstance(Type.GetTypeFromProgID("iTunes.Application"));
                st.ITunesRunning = true;
                sources = Com.Prop(itunes, "Sources");
                int count = Com.Int(sources, "Count");
                for (int i = 1; i <= count; i++)
                {
                    object src = Com.Prop(sources, "Item", i);
                    if (Com.Int(src, "Kind") == SourceKindIPod)
                    {
                        st.Connected = true;
                        st.DeviceName = Com.Text(src, "Name");
                        object pls = Com.Prop(src, "Playlists");
                        int pc = Com.Int(pls, "Count");
                        for (int k = 1; k <= pc; k++)
                        {
                            object pl = Com.Prop(pls, "Item", k);
                            if (Com.Int(pl, "Kind") == PlaylistKindLibrary)
                            {
                                st.TrackCount = Com.Int(Com.Prop(pl, "Tracks"), "Count");
                                Com.Release(pl);
                                break;
                            }
                            Com.Release(pl);
                        }
                        Com.Release(pls);
                        Com.Release(src);
                        break;
                    }
                    Com.Release(src);
                }
                if (!st.Connected) st.Message = "No iPod detected. Plug it in and let iTunes see it.";
            }
            catch (Exception ex)
            {
                st.Message = "Could not talk to iTunes: " + ex.Message;
            }
            finally
            {
                Com.Release(sources);
                Com.Release(itunes);
            }
            return st;
        }

        // =================================================================
        //  What is already ON the device
        // =================================================================

        /// <summary>One item sitting on the iPod. Identified by TrackDatabaseID, which
        /// stays put while list positions shift as things are added and removed.</summary>
        public class DeviceTrack
        {
            public int DatabaseId;
            public string Name = "";
            public string Artist = "";
            public string Album = "";
            public long SizeBytes;
            public int DurationSec;
            public bool IsVideo;

            public string Display
            {
                get { return string.IsNullOrEmpty(Artist) ? Name : Artist + " - " + Name; }
            }

            public string Length
            {
                get
                {
                    if (DurationSec <= 0) return "";
                    return (DurationSec / 60) + ":" + (DurationSec % 60).ToString("00");
                }
            }
        }

        /// <summary>Reads everything on the connected iPod. Must run on an STA thread.</summary>
        public static List<DeviceTrack> ListDeviceTracks(IJobSink sink)
        {
            var list = new List<DeviceTrack>();
            if (!ITunesInstalled())
                throw new Exception(NotInstalledMessage());
            if (!ITunesRunning())
                throw new Exception("iTunes is not running. Start it, plug the iPod in, then refresh.");

            object itunes = Activator.CreateInstance(Type.GetTypeFromProgID("iTunes.Application"));
            object ipod = null, playlist = null;
            try
            {
                ipod = FindIPod(itunes);
                if (ipod == null)
                    throw new Exception("No iPod detected. Plug it in and let iTunes see it.");

                playlist = FindLibraryPlaylist(ipod);
                if (playlist == null)
                    throw new Exception("Could not open the iPod's library playlist.");

                object tracks = Com.Prop(playlist, "Tracks");
                int count = Com.Int(tracks, "Count");
                if (sink != null) sink.Log("Reading " + count + " item(s) from " + Com.Text(ipod, "Name") + "...");

                for (int i = 1; i <= count; i++)
                {
                    if (sink != null && sink.Token.IsCancellationRequested) break;
                    if (sink != null && (i % 50 == 0 || i == count))
                        sink.Progress(i * 100.0 / Math.Max(1, count));

                    object t = null;
                    try
                    {
                        t = Com.Prop(tracks, "Item", i);
                        var dt = new DeviceTrack();
                        dt.DatabaseId = Com.Int(t, "TrackDatabaseID");
                        dt.Name = Com.Text(t, "Name");
                        dt.Artist = Com.Text(t, "Artist");
                        dt.Album = Com.Text(t, "Album");
                        try { dt.SizeBytes = Convert.ToInt64(Com.Prop(t, "Size")); }
                        catch { }
                        try { dt.DurationSec = Com.Int(t, "Duration"); }
                        catch { }

                        // VideoKind: 0 = none. Not every track object exposes it.
                        try { dt.IsVideo = Com.Int(t, "VideoKind") != 0; }
                        catch
                        {
                            string kind = "";
                            try { kind = Com.Text(t, "KindAsString"); }
                            catch { }
                            dt.IsVideo = kind.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0
                                      || kind.IndexOf("movie", StringComparison.OrdinalIgnoreCase) >= 0;
                        }

                        list.Add(dt);
                    }
                    catch { }
                    finally { Com.Release(t); }
                }
                Com.Release(tracks);
            }
            finally
            {
                Com.Release(playlist);
                Com.Release(ipod);
                Com.Release(itunes);
            }
            return list;
        }

        /// <summary>Removes the given tracks from the iPod, then commits the database.
        /// Walks backwards so removals do not shift the positions still to come.</summary>
        public static int DeleteDeviceTracks(List<int> databaseIds, IJobSink sink)
        {
            if (databaseIds == null || databaseIds.Count == 0) return 0;

            var wanted = new Dictionary<int, bool>();
            foreach (int id in databaseIds) wanted[id] = true;

            object itunes = Activator.CreateInstance(Type.GetTypeFromProgID("iTunes.Application"));
            object ipod = null, playlist = null;
            int removed = 0;

            try
            {
                ipod = FindIPod(itunes);
                if (ipod == null) throw new Exception("No iPod detected.");
                playlist = FindLibraryPlaylist(ipod);
                if (playlist == null) throw new Exception("Could not open the iPod's library playlist.");

                object tracks = Com.Prop(playlist, "Tracks");
                int count = Com.Int(tracks, "Count");
                sink.Stage("Removing " + databaseIds.Count + " item(s)");

                for (int i = count; i >= 1; i--)
                {
                    if (sink.Token.IsCancellationRequested) break;
                    object t = null;
                    try
                    {
                        t = Com.Prop(tracks, "Item", i);
                        int id = Com.Int(t, "TrackDatabaseID");
                        if (wanted.ContainsKey(id))
                        {
                            string label = Com.Text(t, "Name");
                            Com.Call(t, "Delete");
                            removed++;
                            sink.Log("Removed " + label);
                            sink.Progress(removed * 100.0 / databaseIds.Count);
                        }
                    }
                    catch (Exception ex) { sink.Log("Could not remove item " + i + ": " + ex.Message); }
                    finally { Com.Release(t); }
                }
                Com.Release(tracks);

                sink.Stage("Updating iPod database");
                try { Com.Call(itunes, "UpdateIPod"); }
                catch
                {
                    try { Com.Call(ipod, "UpdateIPod"); }
                    catch { }
                }
                sink.Log("Removed " + removed + " item(s) from the iPod.");
            }
            finally
            {
                Com.Release(playlist);
                Com.Release(ipod);
                Com.Release(itunes);
            }
            return removed;
        }

        static object FindIPod(object itunes)
        {
            object sources = Com.Prop(itunes, "Sources");
            int count = Com.Int(sources, "Count");
            for (int i = 1; i <= count; i++)
            {
                object src = Com.Prop(sources, "Item", i);
                if (Com.Int(src, "Kind") == SourceKindIPod) { Com.Release(sources); return src; }
                Com.Release(src);
            }
            Com.Release(sources);
            return null;
        }

        static object FindLibraryPlaylist(object ipod)
        {
            object pls = Com.Prop(ipod, "Playlists");
            int pc = Com.Int(pls, "Count");
            for (int k = 1; k <= pc; k++)
            {
                object pl = Com.Prop(pls, "Item", k);
                if (Com.Int(pl, "Kind") == PlaylistKindLibrary) { Com.Release(pls); return pl; }
                Com.Release(pl);
            }
            Com.Release(pls);
            return null;
        }

        public static List<FileInfo> PendingMusic(Config cfg)
        {
            return ScanWithOverride(cfg.MusicRoot, cfg.MusicIncoming,
                                    new[] { ".m4a", ".mp3", ".aac" });
        }

        public static List<FileInfo> PendingVideos(Config cfg)
        {
            return ScanWithOverride(cfg.VideoRoot, cfg.VideoIncoming,
                                    new[] { ".mp4", ".m4v", ".mov" });
        }

        /// <summary>Downloads can be pointed at a folder outside the library. Sync has to
        /// look there as well, or files would land somewhere it never checks.</summary>
        static List<FileInfo> ScanWithOverride(string root, string incoming, string[] extensions)
        {
            List<FileInfo> found = Scan(root, extensions);

            if (!string.IsNullOrWhiteSpace(incoming) && !IsInside(incoming, root))
            {
                var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (FileInfo f in found) seen[f.FullName] = true;

                foreach (FileInfo f in Scan(incoming, extensions))
                    if (!seen.ContainsKey(f.FullName)) found.Add(f);

                found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
            return found;
        }

        public static bool IsInside(string child, string parent)
        {
            try
            {
                string c = Path.GetFullPath(child).TrimEnd('\\', '/') + "\\";
                string p = Path.GetFullPath(parent).TrimEnd('\\', '/') + "\\";
                return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static List<FileInfo> Scan(string root, string[] extensions)
        {
            var list = new List<FileInfo>();
            if (!Directory.Exists(root)) return list;
            try
            {
                foreach (string f in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(root.Length).Replace('/', '\\');
                    if (rel.IndexOf("\\Synced\\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    foreach (string e in extensions)
                        if (ext == e) { list.Add(new FileInfo(f)); break; }
                }
            }
            catch { }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public static List<FileInfo> Synced(string syncedRoot)
        {
            var list = new List<FileInfo>();
            if (!Directory.Exists(syncedRoot)) return list;
            try
            {
                foreach (string f in Directory.GetFiles(syncedRoot, "*.*", SearchOption.AllDirectories))
                    list.Add(new FileInfo(f));
            }
            catch { }
            list.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
            return list;
        }

        /// <summary>Transfers every pending file of the given kind to the iPod.
        /// Mirrors sync_music.ps1 / sync_videos.ps1 including the library fallback.</summary>
        public static int Transfer(bool music, Config cfg, IJobSink sink)
        {
            var files = music ? PendingMusic(cfg) : PendingVideos(cfg);
            if (files.Count == 0)
            {
                sink.Log("Nothing pending - the Incoming folder is empty.");
                return 0;
            }

            sink.Log("Found " + files.Count + " file(s) to transfer.");
            sink.Stage("Connecting to iTunes");
            sink.Progress(-1);

            if (!ITunesInstalled())
                throw new Exception(IsElevated()
                    ? NotInstalledMessage()
                    : "iTunes is not installed. The sync step needs iTunes to talk to the iPod.");

            object itunes = Activator.CreateInstance(Type.GetTypeFromProgID("iTunes.Application"));
            object ipod = null, playlist = null;

            try
            {
                object sources = Com.Prop(itunes, "Sources");
                int count = Com.Int(sources, "Count");
                for (int i = 1; i <= count; i++)
                {
                    object src = Com.Prop(sources, "Item", i);
                    if (Com.Int(src, "Kind") == SourceKindIPod) { ipod = src; break; }
                    Com.Release(src);
                }
                if (ipod == null)
                    throw new Exception("No iPod found. Plug it in, wait for iTunes to show it, then try again.");

                string device = Com.Text(ipod, "Name");
                sink.Log("Device: " + device);

                object pls = Com.Prop(ipod, "Playlists");
                int pc = Com.Int(pls, "Count");
                for (int k = 1; k <= pc; k++)
                {
                    object pl = Com.Prop(pls, "Item", k);
                    if (Com.Int(pl, "Kind") == PlaylistKindLibrary) { playlist = pl; break; }
                    Com.Release(pl);
                }
                if (playlist == null)
                    throw new Exception("Could not open the iPod's library playlist.");

                string syncedRoot = Path.Combine(music ? cfg.MusicSynced : cfg.VideoSynced, AppPaths.Today);
                Directory.CreateDirectory(syncedRoot);

                int ok = 0, failed = 0, index = 0;
                foreach (var file in files)
                {
                    if (sink.Token.IsCancellationRequested) break;
                    index++;
                    sink.Stage("Transferring " + index + " of " + files.Count);
                    sink.Progress((index - 1) * 100.0 / files.Count);
                    sink.Log("-> " + file.Name);

                    string source = file.FullName;
                    bool success = false;
                    try
                    {
                        int before = Com.Int(Com.Prop(playlist, "Tracks"), "Count");

                        object status = Com.Call(playlist, "AddFile", source);
                        WaitFor(status, sink);
                        Thread.Sleep(400);

                        int after = Com.Int(Com.Prop(playlist, "Tracks"), "Count");
                        if (after > before)
                        {
                            success = true;
                        }
                        else
                        {
                            // Fallback: import into the PC library, then push the track across
                            sink.Log("   direct transfer refused, going through the iTunes library...");
                            object libPl = Com.Prop(itunes, "LibraryPlaylist");
                            object st2 = Com.Call(libPl, "AddFile", source);
                            WaitFor(st2, sink);

                            object tracks = st2 == null ? null : Com.Prop(st2, "Tracks");
                            if (tracks != null && Com.Int(tracks, "Count") > 0)
                            {
                                object track = Com.Prop(tracks, "Item", 1);
                                object added = null;
                                try { added = Com.Call(playlist, "AddTrack", track); } catch { }
                                if (added != null)
                                {
                                    success = true;
                                    if (cfg.DeleteFromLibraryAfterTransfer)
                                    {
                                        try { Com.Call(track, "Delete"); } catch { }
                                    }
                                }
                                else
                                {
                                    sink.Log("   iPod is set to automatic sync - left in the iTunes library for the next sync.");
                                    success = true;
                                }
                                Com.Release(track);
                            }
                            Com.Release(tracks);
                            Com.Release(st2);
                            Com.Release(libPl);
                        }
                        Com.Release(status);
                    }
                    catch (Exception ex)
                    {
                        sink.Log("   error: " + ex.Message);
                    }

                    if (success)
                    {
                        // Only now is it safe to call this one synced.
                        try
                        {
                            string dest = Util.UniquePath(Path.Combine(syncedRoot, file.Name));
                            File.Move(source, dest);
                        }
                        catch (Exception ex)
                        {
                            sink.Log("   on the iPod, but could not be archived: " + ex.Message);
                        }
                        ok++;
                        sink.Log("   done.");
                    }
                    else
                    {
                        failed++;
                        sink.Log("   failed - left in Incoming to try again.");
                    }
                }

                sink.Stage("Updating iPod database");
                sink.Progress(99);
                try { Com.Call(itunes, "UpdateIPod"); }
                catch
                {
                    try { Com.Call(ipod, "UpdateIPod"); }
                    catch (Exception ex) { sink.Log("Database update warning: " + ex.Message); }
                }

                CleanEmptyFolders(music ? cfg.MusicIncoming : cfg.VideoIncoming);

                sink.Progress(100);
                sink.Stage("Done");
                sink.Log("Transferred " + ok + " file(s)" + (failed > 0 ? ", " + failed + " failed." : "."));
                return ok;
            }
            finally
            {
                Com.Release(playlist);
                Com.Release(ipod);
                Com.Release(itunes);
            }
        }

        static void WaitFor(object status, IJobSink sink)
        {
            if (status == null) return;
            int guard = 0;
            while (Com.Flag(status, "InProgress") && guard++ < 4000)
            {
                if (sink.Token.IsCancellationRequested) return;
                Thread.Sleep(200);
            }
        }

        static void CleanEmptyFolders(string root)
        {
            if (!Directory.Exists(root)) return;
            try
            {
                foreach (string dir in Directory.GetDirectories(root))
                {
                    if (Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories).Length == 0)
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }
            }
            catch { }
        }
    }
}
