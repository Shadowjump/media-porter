using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace MediaPorter
{
    public enum NoticeResult { Primary, Secondary, Dismissed }

    /// <summary>A small modal in the app's own styling, used for the startup device
    /// check. Falls back to a plain MessageBox if the XAML is missing, so a broken
    /// or deleted ui file can never stop the app from starting.</summary>
    public static class Notice
    {
        public static NoticeResult Show(Window owner, string title, string body,
                                        string primary, string secondary,
                                        bool offerDontAsk, out bool dontAskAgain)
        {
            dontAskAgain = false;

            string xamlPath = Path.Combine(AppPaths.UiDir, "Notice.xaml");
            if (!File.Exists(xamlPath))
            {
                var fallback = MessageBox.Show(owner, body, title,
                    MessageBoxButton.OKCancel, MessageBoxImage.Information);
                return fallback == MessageBoxResult.OK ? NoticeResult.Primary : NoticeResult.Secondary;
            }

            Window win;
            try
            {
                using (var fs = File.OpenRead(xamlPath))
                    win = (Window)XamlReader.Load(fs);
            }
            catch
            {
                MessageBox.Show(owner, body, title, MessageBoxButton.OK, MessageBoxImage.Information);
                return NoticeResult.Dismissed;
            }

            win.Owner = owner;
            win.WindowStyle = WindowStyle.None;
            win.ResizeMode = ResizeMode.NoResize;
            win.AllowsTransparency = false;
            WindowChrome.SetWindowChrome(win, new WindowChrome
            {
                CaptionHeight = 52,
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

            var titleBlock = win.FindName("NoticeTitle") as TextBlock;
            var bodyBlock = win.FindName("NoticeBody") as TextBlock;
            var okButton = win.FindName("NoticeOk") as Button;
            var altButton = win.FindName("NoticeAlt") as Button;
            var dontAsk = win.FindName("NoticeDontAsk") as CheckBox;

            if (titleBlock != null) titleBlock.Text = title;
            if (bodyBlock != null) bodyBlock.Text = body;
            if (okButton != null) okButton.Content = primary;

            if (altButton != null)
            {
                if (string.IsNullOrEmpty(secondary)) altButton.Visibility = Visibility.Collapsed;
                else altButton.Content = secondary;
            }
            if (dontAsk != null && !offerDontAsk) dontAsk.Visibility = Visibility.Collapsed;

            var result = NoticeResult.Dismissed;
            if (okButton != null) okButton.Click += (s, e) => { result = NoticeResult.Primary; win.DialogResult = true; };
            if (altButton != null) altButton.Click += (s, e) => { result = NoticeResult.Secondary; win.DialogResult = true; };
            win.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { result = NoticeResult.Secondary; win.DialogResult = true; }
            };

            win.ShowDialog();

            if (dontAsk != null) dontAskAgain = dontAsk.IsChecked == true;
            return result;
        }
    }
}
