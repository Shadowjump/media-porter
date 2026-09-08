using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Shell;

namespace MediaPorter
{
    /// <summary>Row shown in the picker - the profile plus the two strings the
    /// template displays, so the XAML needs no converters.</summary>
    public class DeviceRow
    {
        public DeviceProfile Device { get; set; }
        public string Name { get; set; }
        public string Years { get; set; }
        public string Note { get; set; }
        public string Encode { get; set; }
        public string Detail { get; set; }
        /// <summary>Music-only devices are listed for completeness but cannot be chosen
        /// as a video target - there is nothing to encode for.</summary>
        public bool Selectable { get; set; }
    }

    public class FamilyGroup
    {
        public string Family { get; set; }
        public List<DeviceRow> Items { get; set; }
    }

    /// <summary>The "choose your device" popup. Returns the chosen id, or null on cancel.</summary>
    public static class DevicePickerWindow
    {
        public static bool Show(Window owner, ref string deviceId, ref bool preferHevc)
        {
            string xamlPath = Path.Combine(AppPaths.UiDir, "DevicePicker.xaml");
            if (!File.Exists(xamlPath))
            {
                MessageBox.Show(owner, "ui\\DevicePicker.xaml is missing.", "MediaPorter");
                return false;
            }

            Window win;
            using (var fs = File.OpenRead(xamlPath))
                win = (Window)XamlReader.Load(fs);

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

            var familyList = (ItemsControl)win.FindName("FamilyList");
            var chkHevc = (CheckBox)win.FindName("ChkHevc");
            var summary = (TextBlock)win.FindName("PickerSummary");
            var btnOk = (Button)win.FindName("BtnPickOk");
            var btnCancel = (Button)win.FindName("BtnPickCancel");

            bool hevc = preferHevc;
            DeviceProfile chosen = Devices.Find(deviceId);
            var boxes = new List<ListBox>();

            Action refresh = () =>
            {
                foreach (ListBox lb in boxes)
                {
                    var rows = (List<DeviceRow>)lb.ItemsSource;
                    foreach (DeviceRow r in rows)
                    {
                        if (!r.Device.Video)
                        {
                            r.Encode = r.Device.Screen;
                            r.Detail = "music only";
                            continue;
                        }
                        r.Encode = r.Device.Resolution;
                        r.Detail = r.Device.UseHevc(hevc)
                            ? "HEVC"
                            : "H.264 " + r.Device.Profile + " " + r.Device.Level;
                    }
                    lb.Items.Refresh();
                }
                if (summary != null && chosen != null)
                    summary.Text = chosen.Name + "  ·  " + chosen.Summary(hevc) +
                                   (chosen.Verified ? "  ·  Apple-verified" : "");
            };

            // Build the grouped list
            var groups = new List<FamilyGroup>();
            foreach (DeviceProfile d in Devices.All)
            {
                FamilyGroup g = groups.Find(x => x.Family == d.Family);
                if (g == null)
                {
                    g = new FamilyGroup { Family = d.Family, Items = new List<DeviceRow>() };
                    groups.Add(g);
                }
                g.Items.Add(new DeviceRow
                {
                    Device = d,
                    Name = d.Name,
                    Years = d.Years,
                    Note = d.Note,
                    Encode = d.Video ? d.Resolution : d.Screen,
                    Detail = d.Video ? "" : "music only",
                    Selectable = d.Video
                });
            }
            familyList.ItemsSource = groups;

            // Selection has to be wired after the containers exist
            win.Loaded += (s, e) =>
            {
                CollectListBoxes(familyList, boxes);
                foreach (ListBox lb in boxes)
                {
                    ListBox self = lb;
                    // A ListBox eats the wheel even with its own scrollbar switched off,
                    // so the outer ScrollViewer never sees it and the list looks stuck
                    // whenever the pointer is over a row. Re-raising the event upwards
                    // works but crawls a line at a time; scrolling the ancestor directly
                    // gives the normal feel.
                    self.PreviewMouseWheel += (s2, e2) =>
                    {
                        ScrollViewer sv = FindParentScrollViewer(self);
                        if (sv == null) return;
                        sv.ScrollToVerticalOffset(sv.VerticalOffset - e2.Delta);
                        e2.Handled = true;
                    };

                    self.SelectionChanged += (s2, e2) =>
                    {
                        var row = self.SelectedItem as DeviceRow;
                        if (row == null) return;
                        if (!row.Selectable) { self.SelectedIndex = -1; return; }
                        chosen = row.Device;
                        // one selection across all the family lists
                        foreach (ListBox other in boxes)
                            if (other != self) other.SelectedIndex = -1;
                        refresh();
                    };

                    var items = (List<DeviceRow>)self.ItemsSource;
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (items[i].Device.Id == chosen.Id) { self.SelectedIndex = i; break; }
                    }
                }
                refresh();
            };

            if (chkHevc != null)
            {
                chkHevc.IsChecked = hevc;
                chkHevc.Click += (s, e) => { hevc = chkHevc.IsChecked == true; refresh(); };
            }

            bool ok = false;
            btnOk.Click += (s, e) => { ok = true; win.DialogResult = true; };
            btnCancel.Click += (s, e) => { win.DialogResult = false; };
            win.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) win.DialogResult = false;
                else if (e.Key == Key.Enter) { ok = true; win.DialogResult = true; }
            };

            win.ShowDialog();

            if (!ok) return false;
            deviceId = chosen.Id;
            preferHevc = hevc;
            return true;
        }

        static ScrollViewer FindParentScrollViewer(DependencyObject node)
        {
            while (node != null)
            {
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
                var sv = node as ScrollViewer;
                if (sv != null) return sv;
            }
            return null;
        }

        static void CollectListBoxes(DependencyObject root, List<ListBox> into)
        {
            if (root == null) return;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                var lb = child as ListBox;
                if (lb != null) into.Add(lb);
                else CollectListBoxes(child, into);
            }
        }
    }
}
