// The overlay window: tray menu, hotkeys, lock/resize, settings window, auto-hide, fuel model.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace SimracingBR
{
    public class HudWindow : Window
    {
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        readonly Settings cfg;
        readonly HudView view = new HudView();
        readonly GameSources games;
        readonly DemoSource demo = new DemoSource();
        readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Render);
        WF.NotifyIcon tray;
        WF.ToolStripMenuItem miLock, miDemo, miAutoHide;
        bool locked = true, useDemo;
        // Settings window: while it is open the HUD stays visible and shows demo data as a live preview
        Window settingsWin;
        bool settingsOpen;
        DispatcherTimer saveTimer;
        readonly List<Action> refreshers = new List<Action>();
        static readonly string[] FlagKeys = { "ShowGraph", "ShowBars", "ShowClutch", "ShowAids", "ShowGear", "ShowSpeed",
            "ShowShiftLights", "ShowWheel", "ShowFuel", "ShowFuelPerLap", "ShowLapsLeft" };
        // auto-hide outside of driving (menus, replays, pause, setup in the pit box, game not focused)
        bool autoHide, hideInPitBox, hideUnfocused, sawLiveStatus;
        ITelemetrySource lastSource;
        DateTime visChangeSince = DateTime.UtcNow;
        uint lastFgPid; bool lastFgIsGame;
        static readonly uint OwnPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        IntPtr hwnd;
        // size: 0.4x .. 3x of the base layout (644 x 162 px)
        const double MinScale = 0.4, MaxScale = 3.0;
        const string App = "SimracingBR", Ver = "0.1.0";
        double scale = 1.0;
        bool resizing;
        readonly List<WF.ToolStripMenuItem> miSizes = new List<WF.ToolStripMenuItem>();
        readonly List<int> sizePcts = new List<int>();
        // fuel-per-lap fallback
        int prevLapMs; float lapStartFuel; double measuredPerLap;

        public HudWindow(Settings s, string dir, bool startDemo)
        {
            cfg = s; useDemo = startDemo || s.Bool("Demo", false);
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
            Opacity = Math.Max(0.2, Math.Min(1, s.Num("Opacity", 0.94)));

            view.CClutch = s.Col("ColorClutch", "#0A30FF"); view.CBrake = s.Col("ColorBrake", "#FF1010");
            view.CThrottle = s.Col("ColorThrottle", "#33D68A");
            view.CAbs = s.Col("ColorAbs", "#FF8A00"); view.CTc = s.Col("ColorTc", "#FFE000");
            view.CShiftA = s.Col("ColorShiftA", "#FF1E1E"); view.CShiftB = s.Col("ColorShiftB", "#28282F");
            autoHide = s.Bool("AutoHide", true); hideInPitBox = s.Bool("HideInPitBox", true);
            hideUnfocused = s.Bool("HideWhenGameNotFocused", true);
            games = new GameSources(s);
            ShowActivated = false;
            view.AidColors = s.Bool("AidColors", true); view.InvertSteer = s.Bool("InvertSteer", false);
            LoadViewFlags();
            view.Wheel = LoadWheel(Path.Combine(dir, s.Str("Wheel", "assets\\wheel.png")));
            double sc = Math.Max(MinScale, Math.Min(MaxScale, s.Num("Scale", 1.0)));
            scale = sc;
            view.LayoutTransform = new ScaleTransform(sc, sc);
            Content = view;

            double x = s.Num("X", double.NaN), y = s.Num("Y", double.NaN);
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (double.IsNaN(x) || double.IsNaN(y))
            {
                x = (SystemParameters.PrimaryScreenWidth - view.Width * sc) / 2;
                y = SystemParameters.PrimaryScreenHeight - view.Height * sc - 40;
            }
            Left = x; Top = y;

            MouseLeftButtonDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseUp;
            MouseWheel += OnWheel;
            SourceInitialized += OnSourceInit;
            Closed += delegate
            {
                SaveState(); if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (hwnd != IntPtr.Zero) for (int id = 1; id <= 5; id++) UnregisterHotKey(hwnd, id);
            };

            BuildTray();
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--settings") >= 0) Loaded += delegate { ShowSettings(); };
            timer.Interval = TimeSpan.FromMilliseconds(16);
            timer.Tick += Tick;
            timer.Start();
        }

        static ImageSource LoadWheel(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bi = new BitmapImage();
                bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(Path.GetFullPath(path)); bi.EndInit();
                bi.Freeze(); return bi;
            }
            catch { return null; }
        }

        void OnSourceInit(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough();
            if (!RegisterHotKey(hwnd, 1, 0x0002 | 0x0004, 0x4C)) // Ctrl+Shift+L
                tray.ShowBalloonTip(5000, App, "Ctrl+Shift+L is used by another app (is another SimracingBR open?). " +
                    "Double-click this tray icon to lock/unlock instead.", WF.ToolTipIcon.Warning);
            // Ctrl+Shift + / -  (main keys and numpad) to resize without unlocking
            RegisterHotKey(hwnd, 2, 0x0006, 0xBB); RegisterHotKey(hwnd, 3, 0x0006, 0xBD);
            RegisterHotKey(hwnd, 4, 0x0006, 0x6B); RegisterHotKey(hwnd, 5, 0x0006, 0x6D);
            HwndSource.FromHwnd(hwnd).AddHook(delegate (IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
            {
                if (msg == 0x0312)
                {
                    int id = w.ToInt32();
                    if (id == 1) { ToggleLock(); handled = true; }
                    else if (id == 2 || id == 4) { Step(1.1); handled = true; }
                    else if (id == 3 || id == 5) { Step(1 / 1.1); handled = true; }
                }
                return IntPtr.Zero;
            });
        }

        // ---- resizing -------------------------------------------------------------------
        // Anchor = HUD center (so it grows in place) unless the user is dragging the corner grip.
        void ApplyScale(double s, bool keepCenter)
        {
            s = Math.Max(MinScale, Math.Min(MaxScale, Math.Round(s, 3)));
            if (Math.Abs(s - scale) < 0.0005) return;
            double oldW = view.Width * scale, oldH = view.Height * scale, newW = view.Width * s, newH = view.Height * s;
            scale = s;
            view.LayoutTransform = new ScaleTransform(s, s);
            if (keepCenter) { Left -= (newW - oldW) / 2; Top -= (newH - oldH) / 2; }
            UpdateSizeMenu();
        }

        void Step(double factor) { ApplyScale(scale * factor, true); SaveState(); }

        void UpdateSizeMenu()
        {
            for (int i = 0; i < miSizes.Count; i++) miSizes[i].Checked = Math.Abs(scale * 100 - sizePcts[i]) < 1.0;
            if (tray != null) tray.Text = App + " " + Ver + "  -  size " + Math.Round(scale * 100) + "%";
        }

        bool OnGrip()
        {
            Point p = Mouse.GetPosition(view);
            return p.X > view.Width - 34 && p.Y > view.Height - 34;
        }

        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (view.Hover && view.LockBtn.Contains(Mouse.GetPosition(view))) { ToggleLock(); e.Handled = true; return; }
            if (locked) return;
            if (OnGrip()) { resizing = true; CaptureMouse(); return; }
            try { DragMove(); } catch { }
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (resizing) { ApplyScale(Mouse.GetPosition(this).X / view.Width, false); return; }
            if (view.Hover && view.LockBtn.Contains(Mouse.GetPosition(view))) Cursor = Cursors.Hand;
            else if (!locked) Cursor = OnGrip() ? Cursors.SizeNWSE : Cursors.SizeAll;
            else Cursor = Cursors.Arrow;
        }

        void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!resizing) return;
            resizing = false; ReleaseMouseCapture(); SaveState();
        }

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (locked) return;
            Step(e.Delta > 0 ? 1.05 : 1 / 1.05);
            e.Handled = true;
        }

        // The locked HUD is click-through, so it never sees the mouse. Instead the cursor is polled every frame:
        // while it is over the HUD the window becomes clickable (still without taking focus from the game)
        // and the lock / unlock button is drawn.
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RC { public int L, T, R, B; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out PT p);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RC r);
        bool hover;

        void UpdateHover()
        {
            bool over = false;
            if (hwnd != IntPtr.Zero && Visibility == Visibility.Visible && !resizing)
            {
                PT p; RC r;
                over = GetCursorPos(out p) && GetWindowRect(hwnd, out r) && p.X >= r.L && p.X < r.R && p.Y >= r.T && p.Y < r.B;
            }
            if (over == hover) return;
            hover = over; view.Hover = over;
            ApplyClickThrough();
            view.InvalidateVisual();
        }

        // Locked: click-through and never takes focus from the game.
        // Unlocked: normal window that can take focus, so the game's mouse capture doesn't block dragging.
        void ApplyClickThrough()
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
            ex = locked ? (hover ? ((ex | WS_EX_NOACTIVATE) & ~WS_EX_TRANSPARENT) : (ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE))
                         : (ex & ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE));
            SetWindowLong(hwnd, GWL_EXSTYLE, ex);
        }

        void ToggleLock()
        {
            locked = !locked;
            view.Unlocked = !locked;
            ApplyClickThrough();
            if (locked) SaveState();
            else { Activate(); Topmost = false; Topmost = true; }
            miLock.Text = locked ? L.T("Unlock position (Ctrl+Shift+L)", "Destravar posição (Ctrl+Shift+L)")
                                 : L.T("Lock position (Ctrl+Shift+L)", "Travar posição (Ctrl+Shift+L)");
            if (locked) { resizing = false; Cursor = Cursors.Arrow; }
            tray.ShowBalloonTip(2500, App, locked ? L.T("Locked. Position and size saved.", "Travado. Posição e tamanho salvos.") :
                L.T("Unlocked: drag to move, scroll or drag the bottom-right corner to resize. Ctrl+Shift+L (or double-click the tray icon) to lock.",
                    "Destravado: arraste para mover, use a rodinha ou arraste o canto inferior direito para redimensionar. Ctrl+Shift+L (ou duplo clique no ícone) para travar."),
                WF.ToolTipIcon.None);
            view.InvalidateVisual();
        }

        void ResetPosition()
        {
            Left = (SystemParameters.PrimaryScreenWidth - view.Width * scale) / 2;
            Top = SystemParameters.PrimaryScreenHeight - view.Height * scale - 40;
            SaveState();
        }

        void SaveState()
        {
            cfg.Set("X", Left.ToString("0", CultureInfo.InvariantCulture));
            cfg.Set("Y", Top.ToString("0", CultureInfo.InvariantCulture));
            cfg.Set("Scale", scale.ToString("0.###", CultureInfo.InvariantCulture));
            cfg.Set("Demo", useDemo ? "1" : "0");
            try { cfg.Save(); } catch { }
        }

        // ---- what to show / layout ------------------------------------------------------
        void LoadViewFlags()
        {
            view.ShowGraph = cfg.Bool("ShowGraph", true); view.ShowBars = cfg.Bool("ShowBars", true);
            view.ShowClutch = cfg.Bool("ShowClutch", true); view.ShowAids = cfg.Bool("ShowAids", true);
            view.ShowGear = cfg.Bool("ShowGear", true); view.ShowSpeed = cfg.Bool("ShowSpeed", true);
            view.ShowShiftLights = cfg.Bool("ShowShiftLights", true); view.ShowWheel = cfg.Bool("ShowWheel", true);
            view.ShowFuel = cfg.Bool("ShowFuel", true); view.ShowFuelPerLap = cfg.Bool("ShowFuelPerLap", true);
            view.ShowLapsLeft = cfg.Bool("ShowLapsLeft", true);
            view.Relayout();
        }

        // After a show/hide change: reflow the HUD and keep its center where it was.
        void LayoutChanged()
        {
            double oldW = view.Width * scale, oldH = view.Height * scale;
            view.Relayout();
            Left -= (view.Width * scale - oldW) / 2; Top -= (view.Height * scale - oldH) / 2;
            view.InvalidateVisual();
            QueueSave();
        }

        void QueueSave()
        {
            if (saveTimer == null)
            {
                saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                saveTimer.Tick += delegate { saveTimer.Stop(); SaveState(); };
            }
            saveTimer.Stop(); saveTimer.Start();
        }

        void ResetSettings()
        {
            double oldW = view.Width * scale, oldH = view.Height * scale;
            foreach (string k in FlagKeys) cfg.Set(k, "1");
            LoadViewFlags();
            Left -= (view.Width * scale - oldW) / 2; Top -= (view.Height * scale - oldH) / 2;
            ApplyScale(1.0, true);
            Opacity = 0.94; cfg.Set("Opacity", "0.94");
            foreach (Action r in refreshers) r();
            view.InvalidateVisual(); QueueSave();
        }

        // ---- settings window -------------------------------------------------------------
        void ShowSettings()
        {
            if (settingsWin != null) { settingsWin.Activate(); return; }
            refreshers.Clear();
            var w = new Window
            {
                Title = L.T("SimracingBR - Settings", "SimracingBR - Configurações"),
                Width = 400, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true,
                Background = new SolidColorBrush(Color.FromRgb(22, 22, 28)), Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"), FontSize = 13
            };
            var root = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };
            w.Content = root;

            root.Children.Add(new TextBlock
            {
                Text = L.T("Changes apply instantly. The HUD shows demo data while this window is open.",
                           "As mudanças valem na hora. O HUD mostra dados de demonstração enquanto esta janela estiver aberta."),
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 160)), TextWrapping = TextWrapping.Wrap, FontSize = 12
            });

            AddSection(root, L.T("PEDALS", "PEDAIS"));
            AddCheck(root, L.T("Input graph", "Gráfico dos pedais"), "ShowGraph", () => view.ShowGraph, v => view.ShowGraph = v);
            AddCheck(root, L.T("Pedal bars", "Barras dos pedais"), "ShowBars", () => view.ShowBars, v => view.ShowBars = v);
            AddCheck(root, L.T("Clutch (bar and graph line)", "Embreagem (barra e linha do gráfico)"), "ShowClutch", () => view.ShowClutch, v => view.ShowClutch = v);
            AddCheck(root, L.T("ABS / TC levels and activity", "Níveis e atuação de ABS / TC"), "ShowAids", () => view.ShowAids, v => view.ShowAids = v);

            AddSection(root, L.T("DASH", "PAINEL"));
            AddCheck(root, L.T("Gear", "Marcha"), "ShowGear", () => view.ShowGear, v => view.ShowGear = v);
            AddCheck(root, L.T("Speed", "Velocidade"), "ShowSpeed", () => view.ShowSpeed, v => view.ShowSpeed = v);
            AddCheck(root, L.T("Shift lights", "Luzes de troca de marcha"), "ShowShiftLights", () => view.ShowShiftLights, v => view.ShowShiftLights = v);
            AddCheck(root, L.T("Steering wheel", "Volante"), "ShowWheel", () => view.ShowWheel, v => view.ShowWheel = v);

            AddSection(root, L.T("FUEL", "COMBUSTÍVEL"));
            AddCheck(root, L.T("Fuel in the tank", "Combustível no tanque"), "ShowFuel", () => view.ShowFuel, v => view.ShowFuel = v);
            AddCheck(root, L.T("Consumption per lap", "Consumo por volta"), "ShowFuelPerLap", () => view.ShowFuelPerLap, v => view.ShowFuelPerLap = v);
            AddCheck(root, L.T("Laps remaining", "Voltas restantes"), "ShowLapsLeft", () => view.ShowLapsLeft, v => view.ShowLapsLeft = v);

            AddSection(root, L.T("APPEARANCE", "APARÊNCIA"));
            AddSlider(root, L.T("Size", "Tamanho"), MinScale * 100, MaxScale * 100, () => scale * 100,
                v => { ApplyScale(v / 100.0, true); QueueSave(); }, v => Math.Round(v) + "%");
            AddSlider(root, L.T("Transparency (lower = more see-through)", "Transparência (menor = mais transparente)"), 20, 100, () => Opacity * 100,
                v => { Opacity = v / 100.0; cfg.Set("Opacity", (v / 100.0).ToString("0.##", CultureInfo.InvariantCulture)); QueueSave(); },
                v => Math.Round(v) + "%");

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var reset = new Button { Content = L.T("Reset to defaults", "Restaurar padrões"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
            reset.Click += delegate { ResetSettings(); };
            var close = new Button { Content = L.T("Close", "Fechar"), Padding = new Thickness(18, 5, 18, 5), IsDefault = true };
            close.Click += delegate { w.Close(); };
            buttons.Children.Add(reset); buttons.Children.Add(close);
            root.Children.Add(buttons);

            w.Closed += delegate { settingsWin = null; settingsOpen = false; SaveState(); };
            settingsWin = w; settingsOpen = true;
            w.Show(); w.Activate();
        }

        static void AddSection(Panel p, string title)
        {
            p.Children.Add(new TextBlock
            {
                Text = title, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 16, 0, 2),
                Foreground = new SolidColorBrush(Color.FromRgb(225, 6, 0))
            });
        }

        void AddCheck(Panel p, string label, string key, Func<bool> get, Action<bool> set)
        {
            var cb = new CheckBox { Content = label, IsChecked = get(), Margin = new Thickness(0, 4, 0, 2), Foreground = Brushes.White };
            RoutedEventHandler h = delegate
            {
                bool v = cb.IsChecked == true;
                set(v); cfg.Set(key, v ? "1" : "0"); LayoutChanged();
            };
            cb.Checked += h; cb.Unchecked += h;
            refreshers.Add(delegate { cb.IsChecked = get(); });
            p.Children.Add(cb);
        }

        void AddSlider(Panel p, string label, double min, double max, Func<double> get, Action<double> apply, Func<double, string> fmt)
        {
            var head = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var val = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(225, 6, 0)), FontWeight = FontWeights.Bold };
            DockPanel.SetDock(val, Dock.Right);
            head.Children.Add(val);
            head.Children.Add(new TextBlock { Text = label });
            var sl = new Slider { Minimum = min, Maximum = max, Value = get(), Margin = new Thickness(0, 4, 0, 0), TickFrequency = 5, IsSnapToTickEnabled = true };
            val.Text = fmt(sl.Value);
            sl.ValueChanged += delegate { val.Text = fmt(sl.Value); apply(sl.Value); };
            refreshers.Add(delegate { sl.Value = get(); });
            p.Children.Add(head); p.Children.Add(sl);
        }

        void BuildTray()
        {
            tray = new WF.NotifyIcon();
            tray.Text = App;
            try
            {
                var bmp = new SD.Bitmap(32, 32);
                using (var g = SD.Graphics.FromImage(bmp))
                {
                    g.Clear(SD.Color.FromArgb(14, 14, 18));
                    g.FillRectangle(new SD.SolidBrush(SD.Color.FromArgb(0, 156, 59)), 0, 0, 6, 32);
                    g.FillRectangle(new SD.SolidBrush(SD.Color.FromArgb(255, 223, 0)), 0, 12, 6, 8);
                    g.DrawString("S", new SD.Font("Segoe UI", 16, SD.FontStyle.Bold), SD.Brushes.White, 8, 3);
                }
                tray.Icon = SD.Icon.FromHandle(bmp.GetHicon());
            }
            catch { tray.Icon = SD.SystemIcons.Application; }
            var menu = new WF.ContextMenuStrip();
            miLock = new WF.ToolStripMenuItem(L.T("Unlock position (Ctrl+Shift+L)", "Destravar posição (Ctrl+Shift+L)"), null, delegate { ToggleLock(); });
            miDemo = new WF.ToolStripMenuItem(L.T("Demo mode", "Modo demonstração"), null, delegate { useDemo = !useDemo; miDemo.Checked = useDemo; });
            miDemo.Checked = useDemo;
            var miSettings = new WF.ToolStripMenuItem(L.T("Settings...", "Configurações..."), null, delegate { ShowSettings(); });
            miSettings.Font = new SD.Font(menu.Font, SD.FontStyle.Bold);
            menu.Items.Add(miSettings);
            menu.Items.Add(miLock);
            var size = new WF.ToolStripMenuItem(L.T("Size", "Tamanho"));
            foreach (int pct in new int[] { 50, 75, 100, 125, 150, 200, 250 })
            {
                int p = pct;
                var mi = new WF.ToolStripMenuItem(p + "%", null, delegate { ApplyScale(p / 100.0, true); SaveState(); });
                size.DropDownItems.Add(mi); miSizes.Add(mi); sizePcts.Add(p);
            }
            size.DropDownItems.Add(new WF.ToolStripSeparator());
            size.DropDownItems.Add(new WF.ToolStripMenuItem(L.T("Bigger (Ctrl+Shift++)", "Maior (Ctrl+Shift++)"), null, delegate { Step(1.1); }));
            size.DropDownItems.Add(new WF.ToolStripMenuItem(L.T("Smaller (Ctrl+Shift+-)", "Menor (Ctrl+Shift+-)"), null, delegate { Step(1 / 1.1); }));
            menu.Items.Add(size);
            menu.Items.Add(new WF.ToolStripMenuItem(L.T("Reset position", "Restaurar posição"), null, delegate { ResetPosition(); }));
            menu.Items.Add(miDemo);
            miAutoHide = new WF.ToolStripMenuItem(L.T("Hide when not driving", "Esconder quando não está pilotando"), null, delegate
            {
                autoHide = !autoHide; miAutoHide.Checked = autoHide;
                cfg.Set("AutoHide", autoHide ? "1" : "0"); SaveState();
            });
            miAutoHide.Checked = autoHide;
            menu.Items.Add(miAutoHide);
            menu.Items.Add(new WF.ToolStripMenuItem(L.T("Quit", "Sair"), null, delegate { Close(); }));
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ToggleLock(); };
            tray.Visible = true;
            UpdateSizeMenu();
        }

        void Tick(object sender, EventArgs e)
        {
            Tele t = view.T;
            if (useDemo || settingsOpen) demo.Read(t);
            else
            {
                bool ok = games.Read(t);
                if (!ok || !t.Live)
                {
                    t.Gas = t.Brake = t.Clutch = 0; t.AbsOn = t.TcOn = t.ShiftUp = false;
                }
                if (games.Active != lastSource) SourceChanged(games.Active);
            }
            bool tcc = useDemo || settingsOpen || t.HasTcCut;
            if (tcc != view.HasTcc) { view.HasTcc = tcc; LayoutChanged(); }
            UpdateFuel(t);
            view.AbsDuty += ((t.AbsOn ? 1.0 : 0.0) - view.AbsDuty) * 0.04;
            view.TcDuty += ((t.TcOn ? 1.0 : 0.0) - view.TcDuty) * 0.04;
            view.Push();
            UpdateVisibility(t);
            UpdateHover();
            if (Visibility == Visibility.Visible) view.InvalidateVisual();
        }

        bool ShouldShow(Tele t)
        {
            if (!locked || useDemo || !autoHide || settingsOpen) return true;
            if (!t.Live) return false;                          // game closed, loading or frozen
            if (t.Status == 2) sawLiveStatus = true;
            if (t.Status == 1 || t.Status == 3) return false;   // replay / pause
            if (t.Status == 0 && sawLiveStatus) return false;   // back to menus (only trusted once we've seen "live")
            if (hideInPitBox && t.InPitBox && t.SpeedKmh < 2) return false;   // setup screen / garage
            if (hideUnfocused && !GameFocused()) return false;
            return true;
        }

        bool GameFocused()
        {
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            if (pid == OwnPid) return true;
            if (pid != lastFgPid)
            {
                lastFgPid = pid;
                lastFgIsGame = false;
                try
                {
                    string name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                    foreach (ITelemetrySource s in games.All)
                        if (lastSource == null || s == lastSource)
                            foreach (string p in s.Processes)
                                if (name.Equals(p, StringComparison.OrdinalIgnoreCase)) lastFgIsGame = true;
                }
                catch { }
            }
            return lastFgIsGame;
        }

        // Another game took over: forget what we learned about the previous one.
        void SourceChanged(ITelemetrySource s)
        {
            lastSource = s;
            sawLiveStatus = false;
            lastFgPid = 0;
            prevLapMs = 0; lapStartFuel = 0; measuredPerLap = 0;
            if (s != null) tray.Text = App + " " + Ver + "  -  " + s.Name;
        }

        // Debounced so the HUD doesn't flicker on short transitions.
        void UpdateVisibility(Tele t)
        {
            bool want = ShouldShow(t);
            bool shown = Visibility == Visibility.Visible;
            if (want == shown) { visChangeSince = DateTime.UtcNow; return; }
            if ((DateTime.UtcNow - visChangeSince).TotalMilliseconds < (want ? 150 : 500)) return;
            Visibility = want ? Visibility.Visible : Visibility.Hidden;
            visChangeSince = DateTime.UtcNow;
        }

        void UpdateFuel(Tele t)
        {
            // lap rollover: current lap time drops back near zero
            if (lapStartFuel <= 0 && t.Fuel > 0) lapStartFuel = t.Fuel;
            if (prevLapMs > 5000 && t.LapMs < prevLapMs - 2000)
            {
                float used = lapStartFuel - t.Fuel;
                if (used > 0.05f && used < 60f) measuredPerLap = measuredPerLap <= 0 ? used : (measuredPerLap * 0.6 + used * 0.4);
                lapStartFuel = t.Fuel;
            }
            prevLapMs = t.LapMs;
            double per = t.FuelPerLap > 0.01f ? t.FuelPerLap : measuredPerLap;
            view.PerLap = per;
            view.LapsLeft = (per > 0.01 && t.Fuel > 0) ? t.Fuel / per : 0;
        }
    }
}
