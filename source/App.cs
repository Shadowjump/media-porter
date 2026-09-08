using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Shell;
using System.Windows.Threading;

namespace MediaPorter
{
    // =====================================================================
    //  View models
    // =====================================================================
    public class Notifier : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    public enum ItemState { Waiting, Running, Done, Failed, Stopped }

    public class QueueItem : Notifier
    {
        public string Url;
        public bool IsVideo;
        public string OutputPath;

        string _title = "", _status = "Waiting in the queue", _badge = "";
        double _percent;
        bool _indeterminate;
        ItemState _state = ItemState.Waiting;

        public string Title
        {
            get { return _title; }
            set { _title = value; Raise("Title"); }
        }

        public string Status
        {
            get { return _status; }
            set { _status = value; Raise("Status"); }
        }

        public string Badge
        {
            get { return _badge; }
            set { _badge = value; Raise("Badge"); }
        }

        public double Percent
        {
            get { return _percent; }
            set { _percent = value; Raise("Percent"); }
        }

        public bool IsIndeterminate
        {
            get { return _indeterminate; }
            set { _indeterminate = value; Raise("IsIndeterminate"); }
        }

        public ItemState State
        {
            get { return _state; }
            set
            {
                _state = value;
                Raise("State"); Raise("StateColor"); Raise("BarVisibility"); Raise("RevealVisibility");
            }
        }

        public string StateColor
        {
            get
            {
                if (_state == ItemState.Running) return "#FF8A3D";
                if (_state == ItemState.Done) return "#34D399";
                if (_state == ItemState.Failed) return "#F87171";
                if (_state == ItemState.Stopped) return "#FBBF24";
                return "#3A4657";
            }
        }

        public string BarVisibility
        {
            get { return _state == ItemState.Running ? "Visible" : "Collapsed"; }
        }

        public string RevealVisibility
        {
            get { return _state == ItemState.Done && !string.IsNullOrEmpty(OutputPath) ? "Visible" : "Collapsed"; }
        }

        public void MarkDone(string path)
        {
            OutputPath = path;
            Percent = 100;
            IsIndeterminate = false;
            State = ItemState.Done;
            Status = "Saved to " + (string.IsNullOrEmpty(path) ? "the library" : Path.GetFileName(Path.GetDirectoryName(path)));
        }
    }

    public class LibRow
    {
        public string Name { get; set; }
        public string Detail { get; set; }
        public string Size { get; set; }
        public string FullPath { get; set; }

        /// <summary>iTunes TrackDatabaseID when this row lives on the iPod, else 0.</summary>
        public int TrackId { get; set; }
        public bool OnDevice { get { return TrackId != 0; } }
    }

    public class ToolRow
    {
        public string Name { get; set; }
        public string Detail { get; set; }
        public string Path { get; set; }
        public string Color { get; set; }
    }

    // =====================================================================
    //  Background job runner - one job at a time, always on an STA thread
    //  because the iTunes COM API insists on it.
    // =====================================================================
    public class JobTask
    {
        public string Label;
        public QueueItem Item;
        public Action<IJobSink> Work;
        public Action<Exception> Finished;
    }

    public class JobRunner
    {
        readonly BlockingCollection<JobTask> _queue = new BlockingCollection<JobTask>();
        CancellationTokenSource _cts;
        readonly object _lock = new object();

        public event Action<JobTask> Started;
        public event Action<JobTask, Exception> Completed;
        public event Action<int> DepthChanged;

        public Func<JobTask, IJobSink> SinkFactory;

        public bool Busy { get; private set; }
        public int Pending { get { return _queue.Count; } }

        public JobRunner()
        {
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        public void Enqueue(JobTask task)
        {
            _queue.Add(task);
            if (DepthChanged != null) DepthChanged(_queue.Count);
        }

        public void CancelCurrent()
        {
            lock (_lock) { if (_cts != null) _cts.Cancel(); }
        }

        void Loop()
        {
            foreach (JobTask task in _queue.GetConsumingEnumerable())
            {
                lock (_lock) { _cts = new CancellationTokenSource(); }
                Busy = true;
                if (Started != null) Started(task);

                Exception error = null;
                try
                {
                    IJobSink sink = SinkFactory(task);
                    task.Work(sink);
                }
                catch (OperationCanceledException)
                {
                    error = new OperationCanceledException("Stopped.");
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    Busy = false;
                    lock (_lock)
                    {
                        if (_cts != null) { _cts.Dispose(); _cts = null; }
                    }
                }

                if (Completed != null) Completed(task, error);
                if (DepthChanged != null) DepthChanged(_queue.Count);
            }
        }

        public CancellationToken Token
        {
            get
            {
                lock (_lock) { return _cts != null ? _cts.Token : CancellationToken.None; }
            }
        }
    }

    /// <summary>Pipes worker output back onto the UI thread without flooding it.</summary>
    public class UiSink : IJobSink
    {
        readonly Dispatcher _d;
        readonly QueueItem _item;
        readonly Action<string> _log;
        readonly Action<string, double, bool> _status;
        DateTime _lastProgress = DateTime.MinValue;
        readonly JobRunner _runner;

        public UiSink(Dispatcher d, JobRunner runner, QueueItem item,
                      Action<string> log, Action<string, double, bool> status)
        {
            _d = d; _runner = runner; _item = item; _log = log; _status = status;
        }

        public CancellationToken Token { get { return _runner.Token; } }

        public void Log(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            _d.BeginInvoke(DispatcherPriority.Background, (Action)(() => _log(line)));
        }

        public void Stage(string text)
        {
            _d.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                if (_item != null) _item.Status = text;
                _status(text, double.NaN, false);
            }));
        }

        public void Progress(double percent)
        {
            var now = DateTime.UtcNow;
            bool special = percent < 0 || percent >= 100;
            if (!special && (now - _lastProgress).TotalMilliseconds < 70) return;
            _lastProgress = now;

            _d.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
            {
                bool indet = percent < 0;
                if (_item != null)
                {
                    _item.IsIndeterminate = indet;
                    if (!indet) _item.Percent = percent;
                }
                _status(null, indet ? double.NaN : percent, indet);
            }));
        }
    }

    // =====================================================================
    //  The application
    // =====================================================================
    public class App : Application
    {
        public static Config Cfg;
        static App _instance;
        static MainUi _ui;

        [STAThread]
        public static void Main()
        {
            var app = new App();
            _instance = app;

            AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);
            app.DispatcherUnhandledException += (s, e) => { Crash(e.Exception); e.Handled = true; };

            Cfg = Config.Load();

            try
            {
                _ui = new MainUi();
            }
            catch (Exception ex)
            {
                MessageBox.Show("MediaPorter could not start.\n\n" + ex.Message +
                                "\n\nMake sure the 'ui' folder sits next to the exe.",
                                "MediaPorter", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            app.Run(_ui.Window);
        }

        public static void RequestShutdown()
        {
            try
            {
                if (_instance != null)
                    _instance.Dispatcher.BeginInvoke((Action)(() => _instance.Shutdown()));
            }
            catch { }
        }

        static void Crash(Exception ex)
        {
            if (ex == null) return;
            try
            {
                File.AppendAllText(AppPaths.LogFile,
                    "\r\n[" + DateTime.Now + "] CRASH\r\n" + ex + "\r\n");
            }
            catch { }
            MessageBox.Show("Something went wrong:\n\n" + ex.Message, "MediaPorter",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // =====================================================================
    //  Window + wiring
    // =====================================================================
    public class MainUi
    {
        public Window Window;

        readonly ObservableCollection<QueueItem> _queue = new ObservableCollection<QueueItem>();
        readonly ObservableCollection<LibRow> _libRows = new ObservableCollection<LibRow>();
        readonly ObservableCollection<ToolRow> _toolRows = new ObservableCollection<ToolRow>();
        readonly JobRunner _runner = new JobRunner();
        readonly List<string> _log = new List<string>();
        List<FileInfo> _libFiles = new List<FileInfo>();
        List<ITunesSync.DeviceTrack> _deviceTracks;
        int _libRequest;
        bool _loadingSettings;

        // ---- named elements -------------------------------------------------
        T F<T>(string name) where T : class { return Window.FindName(name) as T; }

        Border RootBorder;
        Grid TitleBar;
        TextBlock TitleHint, StatusText, MusicEmpty, VideoEmpty, LibEmpty, DeviceName, DeviceInfo,
                  MusicOutHint, VideoOutHint, SyncMusicSize, SyncVideoSize, SyncMusicCount, SyncVideoCount,
                  LibraryPath, AboutText, MusicPlaceholder, VideoPlaceholder, LibFilterPlaceholder;
        Ellipse StatusDot, DeviceDot;
        ProgressBar StatusBar2;
        TextBox TxtMusicUrl, TxtVideoUrl, TxtSuiteRoot, TxtLibFilter, SyncLog, LogMusic, LogVideo;
        ListBox LibList;
        ItemsControl QueueMusic, QueueVideo, ToolList;
        ScrollViewer MusicQueueScroll, VideoQueueScroll;
        CheckBox ChkEnrich, ChkNormalize, ChkAutoUpdate;
        ComboBox CmbLibScope;
        RadioButton NavMusic, NavVideo, NavSync, NavLibrary, NavSettings;
        Grid PageMusic, PageVideo, PageSync, PageLibrary, PageSettings;

        public MainUi()
        {
            string xaml = Path.Combine(AppPaths.UiDir, "MainWindow.xaml");
            if (!File.Exists(xaml))
                throw new FileNotFoundException("ui\\MainWindow.xaml is missing.", xaml);

            using (var fs = File.OpenRead(xaml))
                Window = (Window)XamlReader.Load(fs);

            Grab();
            SetUpChrome();
            WireNav();
            WireMusic();
            WireVideo();
            WireSync();
            WireLibrary();
            WireSettings();
            WireRunner();

            Window.Loaded += (s, e) =>
            {
                ApplyDwm();
                MakeIcon();
                LoadSettingsIntoUi();
                RefreshCounts();
                RefreshLibrary();
                RefreshTools();
                bool askedAboutTools = OfferToolDownload();
                CheckDevice(false, !askedAboutTools);
                AppendLog("MediaPorter ready. Library: " + App.Cfg.SuiteRoot);
                if (!askedAboutTools) CheckForToolUpdates();
            };

            Window.Closing += (s, e) => App.Cfg.Save();
        }

        void Grab()
        {
            RootBorder = F<Border>("RootBorder");
            TitleBar = F<Grid>("TitleBar");
            TitleHint = F<TextBlock>("TitleHint");
            StatusText = F<TextBlock>("StatusText");
            StatusDot = F<Ellipse>("StatusDot");
            StatusBar2 = F<ProgressBar>("StatusBar2");
            DeviceDot = F<Ellipse>("DeviceDot");
            DeviceName = F<TextBlock>("DeviceName");
            DeviceInfo = F<TextBlock>("DeviceInfo");

            MusicEmpty = F<TextBlock>("MusicEmpty");
            VideoEmpty = F<TextBlock>("VideoEmpty");
            LibEmpty = F<TextBlock>("LibEmpty");
            MusicOutHint = F<TextBlock>("MusicOutHint");
            VideoOutHint = F<TextBlock>("VideoOutHint");
            SyncMusicCount = F<TextBlock>("SyncMusicCount");
            SyncVideoCount = F<TextBlock>("SyncVideoCount");
            SyncMusicSize = F<TextBlock>("SyncMusicSize");
            SyncVideoSize = F<TextBlock>("SyncVideoSize");
            LibraryPath = F<TextBlock>("LibraryPath");
            AboutText = F<TextBlock>("AboutText");
            MusicPlaceholder = F<TextBlock>("MusicPlaceholder");
            VideoPlaceholder = F<TextBlock>("VideoPlaceholder");
            LibFilterPlaceholder = F<TextBlock>("LibFilterPlaceholder");

            TxtMusicUrl = F<TextBox>("TxtMusicUrl");
            TxtVideoUrl = F<TextBox>("TxtVideoUrl");
            TxtSuiteRoot = F<TextBox>("TxtSuiteRoot");
            TxtLibFilter = F<TextBox>("TxtLibFilter");
            SyncLog = F<TextBox>("SyncLog");
            LogMusic = F<TextBox>("LogMusic");
            LogVideo = F<TextBox>("LogVideo");

            LibList = F<ListBox>("LibList");
            QueueMusic = F<ItemsControl>("QueueMusic");
            QueueVideo = F<ItemsControl>("QueueVideo");
            ToolList = F<ItemsControl>("ToolList");
            MusicQueueScroll = F<ScrollViewer>("MusicQueueScroll");
            VideoQueueScroll = F<ScrollViewer>("VideoQueueScroll");

            ChkEnrich = F<CheckBox>("ChkEnrich");
            ChkNormalize = F<CheckBox>("ChkNormalize");
            ChkAutoUpdate = F<CheckBox>("ChkAutoUpdate");

            CmbLibScope = F<ComboBox>("CmbLibScope");

            NavMusic = F<RadioButton>("NavMusic");
            NavVideo = F<RadioButton>("NavVideo");
            NavSync = F<RadioButton>("NavSync");
            NavLibrary = F<RadioButton>("NavLibrary");
            NavSettings = F<RadioButton>("NavSettings");

            PageMusic = F<Grid>("PageMusic");
            PageVideo = F<Grid>("PageVideo");
            PageSync = F<Grid>("PageSync");
            PageLibrary = F<Grid>("PageLibrary");
            PageSettings = F<Grid>("PageSettings");

            QueueMusic.ItemsSource = _queue;
            QueueVideo.ItemsSource = _queue;
            LibList.ItemsSource = _libRows;
            ToolList.ItemsSource = _toolRows;
        }

        // -----------------------------------------------------------------
        //  Window chrome: custom title bar, snap, rounded corners, dark mode
        // -----------------------------------------------------------------
        void SetUpChrome()
        {
            Window.WindowStyle = WindowStyle.None;
            Window.ResizeMode = ResizeMode.CanResize;
            Window.AllowsTransparency = false;

            // CaptionHeight lets Windows own the drag, double-click-to-maximise and
            // Aero Snap. Anything clickable up there has to opt back into hit testing.
            var chrome = new WindowChrome
            {
                CaptionHeight = 46,
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(6),
                UseAeroCaptionButtons = false
            };
            WindowChrome.SetWindowChrome(Window, chrome);

            var btnMin = F<Button>("BtnMin");
            var btnMax = F<Button>("BtnMax");
            var btnClose = F<Button>("BtnClose");
            // WindowChrome checks the flag on whatever element the hit test lands on -
            // it does not walk up the tree - so every visual inside the button needs it.
            Window.Loaded += (s, e) =>
            {
                foreach (Button b in new[] { btnMin, btnMax, btnClose })
                {
                    b.ApplyTemplate();
                    MarkHitTestVisible(b);
                }
            };

            btnMin.Click += (s, e) => Window.WindowState = WindowState.Minimized;
            btnMax.Click += (s, e) => ToggleMax();
            btnClose.Click += (s, e) => Window.Close();
            F<Button>("BtnCancel").Click += (s, e) =>
            {
                _runner.CancelCurrent();
                SetStatus("Stopping...", double.NaN, false);
            };

            Window.StateChanged += (s, e) =>
            {
                bool max = Window.WindowState == WindowState.Maximized;
                RootBorder.CornerRadius = new CornerRadius(max ? 0 : 10);
                RootBorder.BorderThickness = new Thickness(max ? 0 : 1);
                // WindowChrome overshoots by the frame size when maximised
                Window.BorderThickness = new Thickness(max ? 7 : 0);
            };

            Window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && _runner.Busy) _runner.CancelCurrent();
            };
        }

        static void MarkHitTestVisible(DependencyObject root)
        {
            if (root == null) return;
            var el = root as UIElement;
            if (el != null) WindowChrome.SetIsHitTestVisibleInChrome(el, true);
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
                MarkHitTestVisible(VisualTreeHelper.GetChild(root, i));
        }

        void ToggleMax()
        {
            Window.WindowState = Window.WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void ApplyDwm()
        {
            try
            {
                IntPtr h = new WindowInteropHelper(Window).Handle;
                int dark = 1;
                DwmSetWindowAttribute(h, 20, ref dark, sizeof(int));   // immersive dark mode
                DwmSetWindowAttribute(h, 19, ref dark, sizeof(int));   // older builds
                int round = 2;
                DwmSetWindowAttribute(h, 33, ref round, sizeof(int));  // rounded corners (Win11)
            }
            catch { }
        }

        /// <summary>Draws the taskbar icon at runtime - no .ico file needed.</summary>
        void MakeIcon()
        {
            try
            {
                string ico = Path.Combine(AppPaths.UiDir, "app.ico");
                if (File.Exists(ico))
                {
                    Window.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(ico));
                    return;
                }

                var dv = new DrawingVisual();
                using (DrawingContext dc = dv.RenderOpen())
                {
                    var grad = new LinearGradientBrush(
                        (Color)ColorConverter.ConvertFromString("#FF3B5C"),
                        (Color)ColorConverter.ConvertFromString("#FF8A3D"),
                        45);
                    dc.DrawRoundedRectangle(grad, null, new Rect(0, 0, 64, 64), 15, 15);
                    dc.DrawEllipse(Brushes.White, null, new Point(32, 32), 17, 17);
                    dc.DrawEllipse(new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString("#151A22")), null, new Point(32, 32), 6, 6);
                }
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                Window.Icon = rtb;
            }
            catch { }
        }

        // -----------------------------------------------------------------
        //  Navigation
        // -----------------------------------------------------------------
        void WireNav()
        {
            NavMusic.Checked += (s, e) => Show(PageMusic);
            NavVideo.Checked += (s, e) => Show(PageVideo);
            NavSync.Checked += (s, e) => { Show(PageSync); RefreshCounts(); };
            NavLibrary.Checked += (s, e) => { Show(PageLibrary); RefreshLibrary(); };
            NavSettings.Checked += (s, e) => { Show(PageSettings); RefreshTools(); };
        }

        void Show(Grid page)
        {
            foreach (Grid g in new[] { PageMusic, PageVideo, PageSync, PageLibrary, PageSettings })
                g.Visibility = g == page ? Visibility.Visible : Visibility.Collapsed;
        }

        // -----------------------------------------------------------------
        //  Music page
        // -----------------------------------------------------------------
        void WireMusic()
        {
            Placeholder(TxtMusicUrl, MusicPlaceholder);

            TxtMusicUrl.KeyDown += (s, e) => { if (e.Key == Key.Enter) AddUrls(TxtMusicUrl, false); };
            F<Button>("BtnMusicAdd").Click += (s, e) => AddUrls(TxtMusicUrl, false);
            F<Button>("BtnMusicOpen").Click += (s, e) =>
                Util.OpenFolder(Path.Combine(App.Cfg.MusicIncoming, AppPaths.Today));
            F<Button>("BtnMusicFolder").Click += (s, e) => PickDownloadFolder(true);
            F<Button>("BtnQueueClear").Click += (s, e) => ClearFinished();
            F<Button>("BtnMusicLog").Click += (s, e) => ToggleLog(LogMusic, MusicQueueScroll, MusicEmpty);

            ChkEnrich.Click += (s, e) => { App.Cfg.EnrichMetadata = ChkEnrich.IsChecked == true; App.Cfg.Save(); };
            ChkNormalize.Click += (s, e) => { App.Cfg.Normalize = ChkNormalize.IsChecked == true; App.Cfg.Save(); };

            QueueMusic.AddHandler(Button.ClickEvent, new RoutedEventHandler(QueueButton));
            QueueVideo.AddHandler(Button.ClickEvent, new RoutedEventHandler(QueueButton));
        }

        void WireVideo()
        {
            Placeholder(TxtVideoUrl, VideoPlaceholder);

            TxtVideoUrl.KeyDown += (s, e) => { if (e.Key == Key.Enter) AddUrls(TxtVideoUrl, true); };
            F<Button>("BtnVideoAdd").Click += (s, e) => AddUrls(TxtVideoUrl, true);
            F<Button>("BtnVideoOpen").Click += (s, e) =>
                Util.OpenFolder(Path.Combine(App.Cfg.VideoIncoming, AppPaths.Today));
            F<Button>("BtnVideoFolder").Click += (s, e) => PickDownloadFolder(false);
            F<Button>("BtnQueueClear2").Click += (s, e) => ClearFinished();
            F<Button>("BtnVideoLog").Click += (s, e) => ToggleLog(LogVideo, VideoQueueScroll, VideoEmpty);
            F<Button>("BtnDeviceVideo").Click += (s, e) => PickDevice();

        }

        void ToggleLog(TextBox log, ScrollViewer queue, TextBlock empty)
        {
            bool showLog = log.Visibility != Visibility.Visible;
            log.Visibility = showLog ? Visibility.Visible : Visibility.Collapsed;
            queue.Visibility = showLog ? Visibility.Collapsed : Visibility.Visible;
            empty.Visibility = showLog ? Visibility.Collapsed : (_queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed);
            if (showLog)
            {
                log.Text = string.Join("\r\n", _log);
                log.ScrollToEnd();
            }
        }

        void QueueButton(object sender, RoutedEventArgs e)
        {
            var btn = e.OriginalSource as Button;
            if (btn == null) return;
            var item = btn.DataContext as QueueItem;
            if (item == null) return;
            string tag = btn.Tag as string;

            if (tag == "reveal") Util.RevealFile(item.OutputPath);
            else if (tag == "remove")
            {
                if (item.State == ItemState.Running) _runner.CancelCurrent();
                else _queue.Remove(item);
                UpdateEmptyStates();
            }
            e.Handled = true;
        }

        void ClearFinished()
        {
            for (int i = _queue.Count - 1; i >= 0; i--)
                if (_queue[i].State != ItemState.Running && _queue[i].State != ItemState.Waiting)
                    _queue.RemoveAt(i);
            UpdateEmptyStates();
        }

        void AddUrls(TextBox box, bool video)
        {
            string raw = box.Text ?? "";
            var urls = raw.Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(Util.NormalizeUrl)
                          .Where(Util.LooksLikeUrl)
                          .ToList();

            if (urls.Count == 0)
            {
                Flash("That does not look like a link. Paste a YouTube address.");
                return;
            }

            box.Text = "";
            foreach (string url in urls) Enqueue(url, video);
        }

        void Enqueue(string url, bool video)
        {
            var item = new QueueItem
            {
                Url = url,
                IsVideo = video,
                Title = ShortUrl(url),
                Badge = video ? "video" : "music",
                Status = "Waiting in the queue"
            };
            _queue.Add(item);
            UpdateEmptyStates();

            var task = new JobTask
            {
                Label = (video ? "Video: " : "Music: ") + ShortUrl(url),
                Item = item,
                Work = sink =>
                {
                    string path = video
                        ? MediaJobs.DownloadVideo(url, App.Cfg, sink)
                        : MediaJobs.DownloadMusic(url, App.Cfg, sink);
                    Window.Dispatcher.Invoke((Action)(() =>
                    {
                        item.Title = Path.GetFileNameWithoutExtension(path);
                        item.MarkDone(path);
                    }));
                }
            };
            _runner.Enqueue(task);
        }

        static string ShortUrl(string url)
        {
            try
            {
                var u = new Uri(url);
                string q = u.Query ?? "";
                int v = q.IndexOf("v=", StringComparison.OrdinalIgnoreCase);
                if (v >= 0)
                {
                    string id = q.Substring(v + 2);
                    int amp = id.IndexOf('&');
                    if (amp > 0) id = id.Substring(0, amp);
                    return u.Host.Replace("www.", "") + " / " + id;
                }
                return u.Host.Replace("www.", "") + u.AbsolutePath;
            }
            catch { return url; }
        }

        void UpdateEmptyStates()
        {
            var vis = _queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (LogMusic.Visibility != Visibility.Visible) MusicEmpty.Visibility = vis;
            if (LogVideo.Visibility != Visibility.Visible) VideoEmpty.Visibility = vis;
        }

        // -----------------------------------------------------------------
        //  Sync page
        // -----------------------------------------------------------------
        void WireSync()
        {
            F<Button>("BtnSyncMusic").Click += (s, e) => StartSync(true);
            F<Button>("BtnSyncVideo").Click += (s, e) => StartSync(false);
            F<Button>("BtnSyncRefresh").Click += (s, e) => { RefreshCounts(); CheckDevice(false); };
            F<Button>("BtnOpenITunes").Click += (s, e) =>
            {
                ITunesSync.LaunchITunes();
                AppendLog("Asked iTunes to start. Give it a few seconds, then hit Refresh.");
            };
            F<Button>("BtnDeviceRefresh").Click += (s, e) => CheckDevice(true);
        }

        /// <summary>First run on a new machine has no yt-dlp or ffmpeg, and nothing
        /// works without them. Rather than letting the first download fail with a
        /// "tool not found", offer to fetch them straight away. Returns true if the
        /// question was asked, so the device check does not stack a second modal.</summary>
        bool OfferToolDownload()
        {
            bool haveYtDlp = Tools.YtDlp != null;
            bool haveFfmpeg = Tools.FFmpeg != null;
            if (haveYtDlp && haveFfmpeg) return false;

            string missing = !haveYtDlp && !haveFfmpeg
                ? "yt-dlp and ffmpeg are"
                : (!haveYtDlp ? "yt-dlp is" : "ffmpeg is");

            string body =
                missing + " not here yet. They do the actual downloading and encoding, " +
                "and nothing works without them.\n\n" +
                "They are not bundled with this app - ffmpeg alone is about 100 MB, and it " +
                "carries its own licence. Fetching them yourself keeps that between you and " +
                "the people who make them.\n\n" +
                "Downloaded straight from the yt-dlp and FFmpeg-Builds releases on GitHub, " +
                "into this app's own folder. Roughly 115 MB, once.";

            bool ignored;
            NoticeResult choice = Notice.Show(Window, "Two tools are missing", body,
                                              "Download them now", "Not now", false, out ignored);

            if (choice == NoticeResult.Primary)
            {
                RunMaintenance("Fetching yt-dlp and ffmpeg", sink => Maintenance.MakePortable(App.Cfg, sink));
                return true;   // a job is starting; do not stack a second modal on it
            }

            AppendLog("Skipped. Settings > Copy tools into this folder will fetch them whenever you want.");
            return false;      // nothing running, so the device check may still speak up
        }

        /// <summary>Explains what is missing before anything is attempted. Downloading
        /// works fine without iTunes, so this never blocks - it informs and steps aside.</summary>
        void ShowDeviceNotice(DeviceStatus st)
        {
            string body;
            if (!ITunesSync.ITunesInstalled())
            {
                body = "iTunes is not installed on this PC.\n\n" +
                       "Downloading and converting still work - everything lands in the Incoming " +
                       "folders. You only need iTunes for the last step, copying onto the device.";
            }
            else if (!st.ITunesRunning)
            {
                body = "iTunes is not running, so no device can be reached yet.\n\n" +
                       "Start iTunes, plug the device in with its cable, and wait until it appears " +
                       "in the iTunes sidebar. Then press Check again in the corner.\n\n" +
                       "Downloading works without any of this - only Send to iPod needs it.";
            }
            else
            {
                body = "iTunes is running, but no device is connected.\n\n" +
                       "Plug the device in and confirm it shows up in iTunes before using " +
                       "Send to iPod. If it is plugged in but not listed, try a different cable " +
                       "or port - a charge-only cable carries no data.\n\n" +
                       "Downloading and converting work regardless.";
            }

            bool dontAsk;
            NoticeResult choice = Notice.Show(Window, "No device connected", body,
                                              "Open iTunes", "Continue anyway", true, out dontAsk);

            if (dontAsk)
            {
                App.Cfg.WarnWhenDeviceMissing = false;
                App.Cfg.Save();
                AppendLog("Startup device check switched off. Settings has it if you want it back.");
            }

            if (choice == NoticeResult.Primary && ITunesSync.ITunesInstalled())
            {
                ITunesSync.LaunchITunes();
                AppendLog("Starting iTunes. Give it a moment, then press Check again.");
            }
        }

        void StartSync(bool music)
        {
            var task = new JobTask
            {
                Label = music ? "Sending music to the iPod" : "Sending videos to the iPod",
                Item = null,
                Work = sink => ITunesSync.Transfer(music, App.Cfg, sink)
            };
            _runner.Enqueue(task);
            NavSync.IsChecked = true;
        }

        void RefreshCounts()
        {
            try
            {
                var m = ITunesSync.PendingMusic(App.Cfg);
                var v = ITunesSync.PendingVideos(App.Cfg);
                SyncMusicCount.Text = m.Count.ToString();
                SyncVideoCount.Text = v.Count.ToString();
                SyncMusicSize.Text = m.Count == 0 ? "nothing pending" : Util.HumanSize(m.Sum(f => f.Length)) + " ready to transfer";
                SyncVideoSize.Text = v.Count == 0 ? "nothing pending" : Util.HumanSize(v.Sum(f => f.Length)) + " ready to transfer";

                MusicOutHint.Text = "saving into " + ShortOut(App.Cfg.MusicIncoming, App.Cfg.MusicIncomingMoved);
                VideoOutHint.Text = "saving into " + ShortOut(App.Cfg.VideoIncoming, App.Cfg.VideoIncomingMoved);

                F<Button>("BtnSyncMusic").IsEnabled = m.Count > 0;
                F<Button>("BtnSyncVideo").IsEnabled = v.Count > 0;
            }
            catch { }
        }

        /// <summary>"Incoming\2026-09-08" normally; the real path when it has been moved
        /// somewhere else, so it is obvious the downloads are not in the usual place.</summary>
        static string ShortOut(string incoming, bool moved)
        {
            if (!moved) return Path.Combine("Incoming", AppPaths.Today);
            try
            {
                string parent = new DirectoryInfo(incoming).Name;
                return Path.Combine(parent, AppPaths.Today) + "  (moved)";
            }
            catch { return incoming; }
        }

        /// <summary>Folder picker for where a media type lands. Choosing the standard
        /// folder again clears the override rather than storing a redundant copy of it.</summary>
        void PickDownloadFolder(bool music)
        {
            string current = music ? App.Cfg.MusicIncoming : App.Cfg.VideoIncoming;
            string picked = PickFolder(current,
                music ? "Where should downloaded music land?"
                      : "Where should downloaded videos land?");
            if (picked == null) return;

            string standard = music ? App.Cfg.MusicIncomingDefault : App.Cfg.VideoIncomingDefault;
            bool backToStandard = string.Equals(
                Path.GetFullPath(picked).TrimEnd('\\'),
                Path.GetFullPath(standard).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);

            try { Directory.CreateDirectory(picked); }
            catch (Exception ex) { Flash("Cannot use that folder: " + ex.Message); return; }

            if (music) App.Cfg.MusicIncomingOverride = backToStandard ? "" : picked;
            else App.Cfg.VideoIncomingOverride = backToStandard ? "" : picked;
            App.Cfg.Save();

            AppendLog((music ? "Music" : "Video") + " downloads now land in " +
                      (music ? App.Cfg.MusicIncoming : App.Cfg.VideoIncoming) +
                      (backToStandard ? "  (back to the standard folder)" : ""));

            RefreshCounts();
            RefreshLibrary();
        }

        void CheckDevice(bool allowLaunch) { CheckDevice(allowLaunch, false); }

        void CheckDevice(bool allowLaunch, bool announceProblem)
        {
            DeviceName.Text = "Checking...";
            DeviceInfo.Text = "";
            var thread = new Thread(() =>
            {
                DeviceStatus st;
                try { st = ITunesSync.Probe(allowLaunch); }
                catch (Exception ex) { st = new DeviceStatus { Message = ex.Message }; }

                Window.Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (st.Connected)
                    {
                        DeviceDot.Fill = Brush("#34D399");
                        DeviceName.Text = st.DeviceName;
                        DeviceInfo.Text = st.TrackCount + " items on the device";
                    }
                    else
                    {
                        DeviceDot.Fill = Brush(st.ITunesRunning ? "#FBBF24" : "#5E6B80");
                        DeviceName.Text = st.ITunesRunning ? "No iPod" : "iTunes offline";
                        DeviceInfo.Text = st.Message;
                        if (announceProblem && App.Cfg.WarnWhenDeviceMissing) ShowDeviceNotice(st);
                    }
                }));
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        // -----------------------------------------------------------------
        //  Library page
        // -----------------------------------------------------------------
        void WireLibrary()
        {
            Placeholder(TxtLibFilter, LibFilterPlaceholder);
            CmbLibScope.SelectedIndex = 0;
            CmbLibScope.SelectionChanged += (s, e) => { if (!_loadingSettings) RefreshLibrary(); };
            TxtLibFilter.TextChanged += (s, e) => ApplyLibFilter();

            F<Button>("BtnLibReveal").Click += (s, e) =>
            {
                var row = LibList.SelectedItem as LibRow;
                if (row != null && row.OnDevice)
                {
                    Flash("That one lives on the iPod, not in a folder on this PC.");
                    return;
                }
                if (row != null) Util.RevealFile(row.FullPath);
                else Util.OpenFolder(LibraryScopePath());
            };

            F<Button>("BtnLibDelete").Click += (s, e) =>
            {
                var rows = LibList.SelectedItems.Cast<LibRow>().ToList();
                if (rows.Count == 0) { Flash("Pick something first."); return; }

                bool onDevice = rows[0].OnDevice;
                string where = onDevice ? "from the iPod" : "from this PC";
                var answer = MessageBox.Show(
                    "Remove " + rows.Count + " item(s) " + where + "?\n\n" +
                    string.Join("\n", rows.Take(6).Select(r => r.Name)) + (rows.Count > 6 ? "\n..." : "") +
                    (onDevice ? "\n\nThis deletes them off the device. Anything archived in Synced stays on the PC."
                              : "\n\nThis cannot be undone."),
                    "MediaPorter", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.OK) return;

                if (onDevice)
                {
                    var ids = rows.Where(r => r.OnDevice).Select(r => r.TrackId).ToList();
                    _runner.Enqueue(new JobTask
                    {
                        Label = "Removing " + ids.Count + " item(s) from the iPod",
                        Work = sink => ITunesSync.DeleteDeviceTracks(ids, sink),
                        Finished = ex => { if (ex == null) RefreshLibrary(); CheckDevice(false); }
                    });
                    return;
                }

                foreach (var r in rows) Util.TryDelete(r.FullPath);
                RefreshLibrary();
                RefreshCounts();
            };

            F<Button>("BtnLibNormalize").Click += (s, e) =>
            {
                var rows = LibList.SelectedItems.Cast<LibRow>()
                    .Where(r => !r.OnDevice
                             && (r.FullPath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
                              || r.FullPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (rows.Count == 0)
                {
                    Flash("Pick some audio files on this PC - tracks already on the iPod cannot be re-encoded in place.");
                    return;
                }

                _runner.Enqueue(new JobTask
                {
                    Label = "Matching volume on " + rows.Count + " track(s)",
                    Work = sink =>
                    {
                        int n = 0;
                        foreach (var r in rows)
                        {
                            sink.Token.ThrowIfCancellationRequested();
                            n++;
                            sink.Progress((n - 1) * 100.0 / rows.Count);
                            MediaJobs.NormalizeFile(r.FullPath, App.Cfg, sink);
                        }
                        sink.Progress(100);
                    },
                    Finished = ex => { RefreshLibrary(); RefreshCounts(); }
                });
            };
        }

        /// <summary>Scopes 0 and 1 read the iPod itself; the rest are folders on this PC.</summary>
        bool DeviceScope { get { return CmbLibScope.SelectedIndex <= 1; } }

        string LibraryScopePath()
        {
            switch (CmbLibScope.SelectedIndex)
            {
                case 2: return App.Cfg.MusicIncoming;
                case 3: return App.Cfg.VideoIncoming;
                case 4: return App.Cfg.MusicSynced;
                case 5: return App.Cfg.VideoSynced;
                default: return "";
            }
        }

        void RefreshLibrary()
        {
            try
            {
                if (DeviceScope) { LoadDeviceLibrary(); return; }

                _deviceTracks = null;
                LibraryPath.Text = LibraryScopePath();
                switch (CmbLibScope.SelectedIndex)
                {
                    case 3: _libFiles = ITunesSync.PendingVideos(App.Cfg); break;
                    case 4: _libFiles = ITunesSync.Synced(App.Cfg.MusicSynced); break;
                    case 5: _libFiles = ITunesSync.Synced(App.Cfg.VideoSynced); break;
                    default: _libFiles = ITunesSync.PendingMusic(App.Cfg); break;
                }
                ApplyLibFilter();
            }
            catch { }
        }

        /// <summary>Pulls the track list off the iPod on a background STA thread -
        /// iTunes COM refuses to work anywhere else, and a big library takes a moment.</summary>
        void LoadDeviceLibrary()
        {
            LibraryPath.Text = "Reading the iPod...";
            _libRows.Clear();
            LibEmpty.Visibility = Visibility.Collapsed;

            bool wantVideo = CmbLibScope.SelectedIndex == 1;

            // Reading the device takes a moment. If the scope is changed again
            // while that is in flight, the slower reply must not overwrite the
            // newer one - the list would then disagree with the dropdown.
            int request = ++_libRequest;

            var t = new Thread(() =>
            {
                List<ITunesSync.DeviceTrack> tracks = null;
                string error = null;
                try { tracks = ITunesSync.ListDeviceTracks(null); }
                catch (Exception ex) { error = ex.Message; }

                Window.Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (request != _libRequest) return;   // superseded

                    if (error != null)
                    {
                        _deviceTracks = null;
                        LibraryPath.Text = error;
                        LibEmpty.Visibility = Visibility.Visible;
                        return;
                    }

                    _deviceTracks = new List<ITunesSync.DeviceTrack>();
                    foreach (ITunesSync.DeviceTrack dt in tracks)
                        if (dt.IsVideo == wantVideo) _deviceTracks.Add(dt);

                    LibraryPath.Text = _deviceTracks.Count + " " + (wantVideo ? "video(s)" : "track(s)") +
                                       " on the device  ·  " +
                                       Util.HumanSize(TotalSize(_deviceTracks));
                    ApplyLibFilter();
                }));
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        static long TotalSize(List<ITunesSync.DeviceTrack> tracks)
        {
            long total = 0;
            foreach (ITunesSync.DeviceTrack t in tracks) total += t.SizeBytes;
            return total;
        }

        void ApplyLibFilter()
        {
            string q = (TxtLibFilter.Text ?? "").Trim();
            _libRows.Clear();

            if (_deviceTracks != null)
            {
                foreach (ITunesSync.DeviceTrack t in _deviceTracks)
                {
                    if (q.Length > 0 &&
                        t.Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                        (t.Album ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    _libRows.Add(new LibRow
                    {
                        Name = t.Display,
                        Detail = string.IsNullOrEmpty(t.Album) ? t.Length : t.Album + "  ·  " + t.Length,
                        Size = t.SizeBytes > 0 ? Util.HumanSize(t.SizeBytes) : "",
                        FullPath = "",
                        TrackId = t.DatabaseId
                    });
                }
                LibEmpty.Visibility = _libRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            foreach (var f in _libFiles)
            {
                if (q.Length > 0 && f.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string folder = "";
                try { folder = new DirectoryInfo(f.DirectoryName).Name; } catch { }
                _libRows.Add(new LibRow
                {
                    Name = Path.GetFileNameWithoutExtension(f.Name),
                    Detail = folder,
                    Size = Util.HumanSize(f.Length),
                    FullPath = f.FullName
                });
            }
            LibEmpty.Visibility = _libRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // -----------------------------------------------------------------
        //  Settings page
        // -----------------------------------------------------------------
        void WireSettings()
        {
            F<Button>("BtnBrowseRoot").Click += (s, e) =>
            {
                string picked = PickFolder(App.Cfg.SuiteRoot, "Where should the iPod library live?");
                if (picked == null) return;
                TxtSuiteRoot.Text = picked;
                ApplyRoot(picked);
            };

            TxtSuiteRoot.LostFocus += (s, e) => ApplyRoot(TxtSuiteRoot.Text);
            TxtSuiteRoot.KeyDown += (s, e) => { if (e.Key == Key.Enter) ApplyRoot(TxtSuiteRoot.Text); };

            F<Button>("BtnOpenRoot").Click += (s, e) => Util.OpenFolder(App.Cfg.SuiteRoot);
            F<Button>("BtnOpenApp").Click += (s, e) => Util.OpenFolder(AppPaths.AppDir);
            F<Button>("BtnOpenLog").Click += (s, e) =>
            {
                try { File.WriteAllText(AppPaths.LogFile, string.Join("\r\n", _log)); } catch { }
                try { Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true }); } catch { }
            };
            F<Button>("BtnToolsRefresh").Click += (s, e) => RefreshTools();
            F<Button>("BtnDeviceSettings").Click += (s, e) => PickDevice();

            ChkAutoUpdate.Click += (s, e) =>
            {
                App.Cfg.AutoUpdateTools = ChkAutoUpdate.IsChecked == true;
                App.Cfg.Save();
            };

            F<Button>("BtnUpdateYtdlp").Click += (s, e) => RunMaintenance("Updating yt-dlp", Maintenance.UpdateYtDlp);
            F<Button>("BtnUpdateFfmpeg").Click += (s, e) => RunMaintenance("Updating ffmpeg", Maintenance.UpdateFFmpeg);
            F<Button>("BtnMakePortable").Click += (s, e) =>
                RunMaintenance("Making the folder self-contained", sink => Maintenance.MakePortable(App.Cfg, sink));

            F<Button>("BtnRebuild").Click += (s, e) =>
            {
                var answer = MessageBox.Show(
                    "Recompile the app from the C# source in APP\\source and restart into the new build?\n\n" +
                    "If the build fails nothing is replaced.",
                    "Rebuild MediaPorter", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (answer != MessageBoxResult.OK) return;
                RunMaintenance("Rebuilding the app", sink => Maintenance.Rebuild(sink, true));
            };

        }

        void ApplyRoot(string path)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0) return;
            if (string.Equals(path, App.Cfg.SuiteRoot, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                Directory.CreateDirectory(path);
                App.Cfg.SuiteRoot = path;
                App.Cfg.EnsureFolders();
                App.Cfg.Save();
                AppendLog("Library folder is now " + path);
                RefreshCounts();
                RefreshLibrary();
                RefreshTools();
            }
            catch (Exception ex)
            {
                Flash("Could not use that folder: " + ex.Message);
                TxtSuiteRoot.Text = App.Cfg.SuiteRoot;
            }
        }

        /// <summary>Modern Explorer-style folder chooser, owned by the main window so it
        /// cannot end up behind it.</summary>
        string PickFolder(string start, string title)
        {
            IntPtr owner = IntPtr.Zero;
            try { owner = new WindowInteropHelper(Window).Handle; }
            catch { }
            return FolderPicker.Pick(owner, start, title);
        }

        void LoadSettingsIntoUi()
        {
            _loadingSettings = true;
            try
            {
                TxtSuiteRoot.Text = App.Cfg.SuiteRoot;
                ChkEnrich.IsChecked = App.Cfg.EnrichMetadata;
                ChkNormalize.IsChecked = App.Cfg.Normalize;
                ChkAutoUpdate.IsChecked = App.Cfg.AutoUpdateTools;


                var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                string where = "running from " + AppPaths.AppDir;
                if (!AppPaths.AppDirWritable)
                    where += "  ·  installed, so settings and tools live in " + AppPaths.WritableRoot;

                AboutText.Text = "MediaPorter " + ver.Major + "." + ver.Minor +
                                 "  ·  " + where +
                                 "  ·  " + (Maintenance.CanRebuild()
                                    ? "the source is here, so this PC can rebuild the app"
                                    : !AppPaths.AppDirWritable
                                        ? "rebuilding needs a portable copy - this folder is read-only"
                                        : "source or compiler missing - rebuilding is not available here");

                TitleHint.Text = App.Cfg.SuiteRoot;

                ShowDevice();
            }
            finally { _loadingSettings = false; }
        }

        void RefreshTools()
        {
            _toolRows.Clear();
            foreach (string[] r in Maintenance.ToolReport(App.Cfg))
            {
                bool missing = r[1] == "missing" || r[1] == "not installed";
                _toolRows.Add(new ToolRow
                {
                    Name = r[0],
                    Detail = r[1],
                    Path = r[2],
                    Color = missing ? "#F87171" : "#34D399"
                });
            }
        }

        /// <summary>Once a day, ask GitHub whether yt-dlp moved on. Never blocks the UI,
        /// never updates behind your back - it offers, you decide.</summary>
        void CheckForToolUpdates()
        {
            if (!App.Cfg.AutoUpdateTools || Tools.YtDlp == null) return;

            DateTime last;
            if (DateTime.TryParse(App.Cfg.LastToolCheck, CultureInfo.InvariantCulture,
                                  DateTimeStyles.None, out last)
                && (DateTime.Now - last).TotalHours < 24) return;

            var t = new Thread(() =>
            {
                string newer = Maintenance.CheckYtDlpUpdate();
                App.Cfg.LastToolCheck = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
                App.Cfg.Save();
                if (newer == null) return;

                Window.Dispatcher.BeginInvoke((Action)(() =>
                {
                    AppendLog("A newer yt-dlp is on GitHub: " + newer);
                    var answer = MessageBox.Show(
                        "yt-dlp " + newer + " is available on GitHub.\n\n" +
                        "Updating keeps YouTube downloads working. It takes a few seconds and only " +
                        "touches the copy in this folder.\n\nUpdate now?",
                        "MediaPorter", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (answer == MessageBoxResult.Yes)
                        RunMaintenance("Updating yt-dlp", Maintenance.UpdateYtDlp);
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>Opens the device popup. The choice is written straight to config,
        /// so it survives a restart and applies to everything downloaded afterwards.</summary>
        void PickDevice()
        {
            string id = App.Cfg.DeviceId;
            bool hevc = App.Cfg.PreferHevc;
            if (!DevicePickerWindow.Show(Window, ref id, ref hevc)) return;

            App.Cfg.DeviceId = id;
            App.Cfg.PreferHevc = hevc;
            App.Cfg.Save();

            ShowDevice();
            AppendLog("Encoding for " + App.Cfg.Device.Name + " - " + App.Cfg.Device.Summary(hevc));
        }

        /// <summary>Pushes the selected device onto the two chips and the settings card.</summary>
        void ShowDevice()
        {
            DeviceProfile d = App.Cfg.Device;
            string summary = d.Summary(App.Cfg.PreferHevc);

            var chipVideo = F<Button>("BtnDeviceVideo");
            if (chipVideo != null) chipVideo.Content = d.Name;

            // Music output does not vary by device - AAC/.m4a is native to all of them.
            var musicChip = F<TextBlock>("MusicFormatChip");
            if (musicChip != null)
                musicChip.Text = "AAC " + App.Cfg.AudioBitrate.Replace("k", " kbps") + " · .m4a";

            var spec = F<TextBlock>("VideoSpec");
            if (spec != null) spec.Text = summary;

            var name = F<TextBlock>("SettingsDeviceName");
            var sspec = F<TextBlock>("SettingsDeviceSpec");
            var note = F<TextBlock>("SettingsDeviceNote");
            if (name != null) name.Text = d.Name;
            if (sspec != null) sspec.Text = summary + "  ·  source frame rate kept, capped at " + d.FpsCap + " fps";
            if (note != null)
                note.Text = d.Note + (d.Verified
                    ? "  (confirmed against Apple's published spec)"
                    : "  (derived from the verified model in the same generation)");
        }

        void RunMaintenance(string label, Action<IJobSink> work)
        {
            _runner.Enqueue(new JobTask
            {
                Label = label,
                Work = work,
                Finished = ex => { RefreshTools(); }
            });
            NavSettings.IsChecked = true;
        }

        // -----------------------------------------------------------------
        //  Runner plumbing
        // -----------------------------------------------------------------
        void WireRunner()
        {
            _runner.SinkFactory = task => new UiSink(
                Window.Dispatcher, _runner, task.Item, AppendLog,
                (text, pct, indet) => SetStatus(text, pct, indet));

            _runner.Started += task => Window.Dispatcher.BeginInvoke((Action)(() =>
            {
                if (task.Item != null)
                {
                    task.Item.State = ItemState.Running;
                    task.Item.Status = "Starting";
                }
                SetStatus(task.Label, 0, true);
                F<Button>("BtnCancel").Visibility = Visibility.Visible;
                StatusBar2.Visibility = Visibility.Visible;
                StatusDot.Fill = Brush("#FF8A3D");
                AppendLog("--- " + task.Label);
            }));

            _runner.Completed += (task, error) => Window.Dispatcher.BeginInvoke((Action)(() =>
            {
                if (error == null)
                {
                    if (task.Item != null && task.Item.State != ItemState.Done)
                        task.Item.MarkDone(task.Item.OutputPath);
                    SetStatus("Finished: " + task.Label, 100, false);
                    StatusDot.Fill = Brush("#34D399");
                }
                else if (error is OperationCanceledException)
                {
                    if (task.Item != null)
                    {
                        task.Item.State = ItemState.Stopped;
                        task.Item.Status = "Stopped";
                    }
                    SetStatus("Stopped", double.NaN, false);
                    StatusDot.Fill = Brush("#FBBF24");
                    AppendLog("Stopped by you.");
                }
                else
                {
                    if (task.Item != null)
                    {
                        task.Item.State = ItemState.Failed;
                        task.Item.Status = error.Message;
                    }
                    SetStatus(error.Message, double.NaN, false);
                    StatusDot.Fill = Brush("#F87171");
                    AppendLog("ERROR: " + error.Message);
                }

                if (!_runner.Busy && _runner.Pending == 0)
                {
                    F<Button>("BtnCancel").Visibility = Visibility.Collapsed;
                    StatusBar2.Visibility = Visibility.Collapsed;
                }

                RefreshCounts();
                if (PageLibrary.Visibility == Visibility.Visible) RefreshLibrary();
                if (task.Finished != null) { try { task.Finished(error); } catch { } }
            }));
        }

        void SetStatus(string text, double pct, bool indeterminate)
        {
            if (!string.IsNullOrEmpty(text)) StatusText.Text = text;
            StatusBar2.IsIndeterminate = indeterminate;
            if (!double.IsNaN(pct)) StatusBar2.Value = pct;
        }

        void AppendLog(string line)
        {
            string stamped = DateTime.Now.ToString("HH:mm:ss") + "  " + line;
            _log.Add(stamped);
            if (_log.Count > 600) _log.RemoveRange(0, 200);

            foreach (TextBox box in new[] { SyncLog, LogMusic, LogVideo })
            {
                if (box == null) continue;
                if (box != SyncLog && box.Visibility != Visibility.Visible) continue;
                box.AppendText(stamped + "\r\n");
                box.ScrollToEnd();
            }
        }

        void Flash(string message)
        {
            SetStatus(message, double.NaN, false);
            StatusDot.Fill = Brush("#FBBF24");
        }

        static Brush Brush(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        static void Placeholder(TextBox box, TextBlock label)
        {
            if (box == null || label == null) return;
            Action sync = () => label.Visibility = string.IsNullOrEmpty(box.Text)
                ? Visibility.Visible : Visibility.Collapsed;
            box.TextChanged += (s, e) => sync();
            sync();
        }
    }
}
