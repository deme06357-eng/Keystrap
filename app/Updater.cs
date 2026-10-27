// Keystrap.Updater: a small standalone WPF window that updates Keystrap in place.
// It checks the latest GitHub release (or a local manifest file for testing), downloads it, closes Keystrap
// politely, swaps the files with a rollback copy, tells the developers' Discord (unless the user turned crash
// reports off), and starts Keystrap again.
//
// Usage: Keystrap.Updater.exe [--dir <install folder>] [--repo owner/name] [--manifest <url or file>] [--no-launch]
//   --dir       folder holding Keystrap.exe (default: the updater's own folder)
//   --repo      GitHub repository whose latest release to install (default: ReleaseRepo below)
//   --manifest  a release JSON (GitHub's format) by URL or file path, instead of asking GitHub
//   --no-launch don't start Keystrap at the end
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Shape = System.Windows.Shapes.Path;

[assembly: AssemblyTitle("Keystrap Updater")]
[assembly: AssemblyProduct("Keystrap")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyInformationalVersion(Keystrap.Updater.Config.Version)]

namespace Keystrap.Updater
{
    static class Config
    {
        // The GitHub repository releases are published to, as "owner/name". Must be public: the updater doesn't sign in.
        // Empty means no update source is set up.
        public const string ReleaseRepo = "k1tt8/Keystrap";
        public const string Version = "v1.1";   // keep in step with AppInfo.Version in Program.cs
        public const string UserAgent = "Keystrap-Updater/" + Version;
    }

    class Options
    {
        public string Dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public string Repo = Config.ReleaseRepo;
        public string Manifest;
        public bool NoLaunch;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                string next = i + 1 < args.Length ? args[i + 1] : null;
                if (a == "--dir" && next != null) { o.Dir = Path.GetFullPath(next).TrimEnd('\\'); i++; }
                else if (a == "--repo" && next != null) { o.Repo = next; i++; }
                else if (a == "--manifest" && next != null) { o.Manifest = next; i++; }
                else if (a == "--no-launch") o.NoLaunch = true;
            }
            return o;
        }
    }

    static class Log
    {
        public static void Write(string message)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Keystrap", "Logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "updater-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
        }
    }

    class Release
    {
        public string Version;      // tag, e.g. "v2"
        public string AssetName;    // Keystrap.zip (the whole folder) or Keystrap.exe
        public string AssetSource;  // URL or local file path
        public long Size;
    }

    static class Versions
    {
        // "v1.2" -> [1, 2]; anything unreadable counts as 0.
        static int[] Parts(string v)
        {
            v = (v ?? "").Trim().TrimStart('v', 'V');
            return v.Split('.').Select(p => { int n; return int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out n) ? n : 0; }).ToArray();
        }

        public static int Compare(string a, string b)
        {
            int[] x = Parts(a), y = Parts(b);
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
                if (p != q) return p.CompareTo(q);
            }
            return 0;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var options = Options.Parse(args);
            Log.Write("Updater " + Config.Version + " starting for " + options.Dir);
            WebhookService.Source = "Keystrap Updater " + Config.Version;
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Write("Updater error: " + e.Exception);
                e.Handled = true;
                var w = app.MainWindow as UpdaterWindow;
                if (w != null) w.ShowError("Something went wrong in the updater. Details are in the Keystrap logs.");
            };
            app.Run(new UpdaterWindow(options));
        }
    }

    class UpdaterWindow : Window
    {
        const double CardW = 350, CardH = 470, ShadowMargin = 22;   // a narrow, tall card; the margin leaves room for its shadow
        const double TrackW = 240;                                   // progress bar and button width
        static readonly Color Mica = C(0x1c1c1f), Card = C(0x26262a), Stroke = C(0x34343a), Fg = C(0xf2f2f4), Fg2 = C(0xb4b4bc),
            Fg3 = C(0x7c7c86), Accent = C(0xfffcc7), Accent2 = C(0xfffba6), OnAccent = C(0x1f1d10), Bad = C(0xff7a7a), Good = C(0x4cc38a);
        static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Text, Segoe UI");
        static readonly FontFamily Display = new FontFamily("Segoe UI Variable Display, Segoe UI");

        static Color C(int rgb) { return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); }
        static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        readonly Options options;
        readonly Grid root = new Grid();
        readonly Border card;
        readonly TextBlock title, status, versionTag;
        readonly Border progressFill;
        readonly ScaleTransform progressScale = new ScaleTransform(0, 1);
        readonly TranslateTransform indeterminate = new TranslateTransform();
        readonly StackPanel errorButtons;
        bool keystrapWasRunning, busy;

        public UpdaterWindow(Options options)
        {
            this.options = options;
            Title = "Keystrap Updater";
            Width = CardW + ShadowMargin * 2;
            Height = CardH + ShadowMargin * 2;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            FontFamily = Ui;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            try { Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location).Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()); } catch { }

            // ----- card: everything centred in one narrow column, top to bottom -----
            var logo = Logo(84);
            logo.HorizontalAlignment = HorizontalAlignment.Center;
            logo.Margin = new Thickness(0, 0, 0, 20);

            title = new TextBlock { Text = "Updating Keystrap", FontFamily = Display, FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = B(Fg), HorizontalAlignment = HorizontalAlignment.Center };
            versionTag = new TextBlock
            {
                FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Foreground = B(Fg3),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
            };

            // Up to three lines, centred, so error messages are never cut off.
            status = new TextBlock
            {
                Text = "Starting…", FontSize = 13.5, Foreground = B(Fg2), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 22), TextWrapping = TextWrapping.Wrap, MaxWidth = 280, MaxHeight = 80, TextTrimming = TextTrimming.CharacterEllipsis,
            };

            progressFill = new Border
            {
                CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new LinearGradientBrush(Accent, Accent2, 0),
                RenderTransformOrigin = new Point(0, 0.5),
            };
            progressFill.RenderTransform = new TransformGroup { Children = { progressScale, indeterminate } };
            var track = new Border { Width = TrackW, Height = 6, CornerRadius = new CornerRadius(3), Background = B(C(0x2e2e33)), ClipToBounds = true, Child = progressFill, HorizontalAlignment = HorizontalAlignment.Center };

            // Stacked, one per row, as wide as the progress bar.
            errorButtons = new StackPanel { Orientation = Orientation.Vertical, Width = TrackW, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
            var retry = MakeButton("Try again", true);
            var dismiss = MakeButton("Close", false);
            dismiss.Margin = new Thickness(0, 0, 0, 8);
            retry.Margin = new Thickness(0);
            retry.Click += (s, e) => { errorButtons.Visibility = Visibility.Collapsed; track.Visibility = Visibility.Visible; var t = RunAsync(); };
            dismiss.Click += (s, e) => CloseUpdater();
            errorButtons.Children.Add(dismiss);
            errorButtons.Children.Add(retry);

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(logo);
            stack.Children.Add(title);
            stack.Children.Add(versionTag);
            stack.Children.Add(status);
            stack.Children.Add(track);
            stack.Children.Add(errorButtons);

            // The X sits in the corner, outside the centred stack.
            var close = new Button
            {
                Content = new Shape { Data = Geometry.Parse("M0,0 L9,9 M9,0 L0,9"), Stroke = B(Fg2), StrokeThickness = 1.4, Width = 9, Height = 9 },
                Width = 30, Height = 30, Cursor = Cursors.Hand, ToolTip = "Close", Focusable = false,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 10, 0),
                Template = FlatButtonTemplate(Colors.Transparent, Color.FromArgb(0x1a, 0xff, 0xff, 0xff), 6),
            };
            close.Click += (s, e) => CloseUpdater();

            var layers = new Grid();
            layers.Children.Add(stack);
            layers.Children.Add(close);

            card = new Border
            {
                Width = CardW, Height = CardH, CornerRadius = new CornerRadius(12),
                Background = B(Mica), BorderBrush = B(Stroke), BorderThickness = new Thickness(1),
                Padding = new Thickness(24), Child = layers,
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.55, Color = Colors.Black },
            };
            card.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
            root.Margin = new Thickness(ShadowMargin);
            root.Children.Add(card);
            Content = root;

            // No entrance animation: the window shows as it is, and the check starts once the first frame is up.
            Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(() => { var t = RunAsync(); }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        // The Keystrap mark: a square tilted 15° with a rounded square cut out (same geometry as the app's logo).
        static FrameworkElement Logo(double size)
        {
            double k = size / 1024;
            var shape = new GeometryGroup { FillRule = FillRule.EvenOdd };
            shape.Children.Add(new RectangleGeometry(new Rect(128 * k, 128 * k, 768 * k, 768 * k)));
            shape.Children.Add(new RectangleGeometry(new Rect(402 * k, 402 * k, 220 * k, 220 * k), 20 * k, 20 * k));
            shape.Transform = new RotateTransform(15, 512 * k, 512 * k);
            return new Shape
            {
                Data = shape, Width = size, Height = size,
                Fill = new LinearGradientBrush(C(0xfffdd0), C(0xfff799), 90),
            };
        }

        static ControlTemplate FlatButtonTemplate(Color normal, Color hover, double radius)
        {
            var border = new FrameworkElementFactory(typeof(Border), "bd");
            border.SetValue(Border.BackgroundProperty, new SolidColorBrush(normal));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            var t = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var trigger = new Trigger { Property = IsMouseOverProperty, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hover), "bd"));
            t.Triggers.Add(trigger);
            return t;
        }

        static Button MakeButton(string text, bool primary)
        {
            return new Button
            {
                Content = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(primary ? OnAccent : Fg) },
                Height = 36, Padding = new Thickness(16, 0, 16, 0), HorizontalAlignment = HorizontalAlignment.Stretch, Cursor = Cursors.Hand,
                Template = FlatButtonTemplate(primary ? Accent2 : Card, primary ? Accent : C(0x2e2e33), 6),
            };
        }

        // ----- status helpers -----
        void Status(string text, Color? color = null)
        {
            status.Text = text;
            status.Foreground = B(color ?? Fg2);
            Log.Write(text);
        }

        void Progress(double? fraction)
        {
            if (fraction == null)
            {
                progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                progressScale.ScaleX = 0.28;
                var slide = new DoubleAnimation(-TrackW * 0.3, TrackW, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                indeterminate.BeginAnimation(TranslateTransform.XProperty, slide);
                return;
            }
            indeterminate.BeginAnimation(TranslateTransform.XProperty, null);
            indeterminate.X = 0;
            progressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(Math.Max(0, Math.Min(1, fraction.Value)), TimeSpan.FromMilliseconds(180)));
        }

        public void ShowError(string message)
        {
            busy = false;
            Progress(0);
            title.Text = "Update didn't finish";
            Status(message, Bad);
            errorButtons.Visibility = Visibility.Visible;
            ((FrameworkElement)progressFill.Parent).Visibility = Visibility.Collapsed;
        }

        void CloseUpdater()
        {
            if (busy) return;   // never stop half-way through swapping files
            // If we closed Keystrap and the update didn't happen, put it back the way we found it.
            if (keystrapWasRunning && !options.NoLaunch && !KeystrapRunning()) LaunchKeystrap();
            Close();
        }

        // ----- the update itself -----
        string KeystrapExe { get { return Path.Combine(options.Dir, "Keystrap.exe"); } }

        bool KeystrapRunning() { return KeystrapProcesses().Count > 0; }

        List<Process> KeystrapProcesses()
        {
            var list = new List<Process>();
            foreach (Process p in Process.GetProcessesByName("Keystrap"))
            {
                try
                {
                    if (string.Equals(Path.GetFullPath(p.MainModule.FileName), Path.GetFullPath(KeystrapExe), StringComparison.OrdinalIgnoreCase)) { list.Add(p); continue; }
                }
                catch (Exception) { }
                p.Dispose();
            }
            return list;
        }

        static string CurrentVersion(string exe)
        {
            if (!File.Exists(exe)) return "v0";
            string v = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
            return string.IsNullOrWhiteSpace(v) ? "v0" : v.Trim();
        }

        async Task RunAsync()
        {
            title.Text = "Updating Keystrap";
            try
            {
                string current = CurrentVersion(KeystrapExe);
                versionTag.Text = current;
                Status("Checking for updates…");
                Progress(null);

                Release release = await GetLatestAsync();
                if (Versions.Compare(release.Version, current) <= 0)
                {
                    title.Text = "Keystrap is up to date";
                    Progress(1);
                    Status("You have the latest version, " + current + ".", Good);
                    await FinishAsync(800);
                    return;
                }

                versionTag.Text = current + "  →  " + release.Version;
                string staged = await DownloadAsync(release);

                // From here until the files are in place, closing the updater is blocked.
                busy = true;
                Status("Closing Keystrap…");
                Progress(null);
                await CloseKeystrapAsync();

                Status("Installing " + release.Version + "…");
                Progress(0.97);
                await Task.Run(() => Install(staged, release));

                title.Text = "Keystrap is updated";
                Progress(1);
                Status("Installed " + release.Version + ". Starting Keystrap…", Good);
                Task notice = NotifyUpdatedAsync(current, release);
                await Task.WhenAll(notice, Task.Delay(900));   // the notice is bounded to a few seconds
                await FinishAsync(0);
            }
            catch (Exception ex)
            {
                Log.Write("Update failed: " + ex);
                ShowError(Friendly(ex));
            }
        }

        static string Friendly(Exception ex)
        {
            if (ex is UpdateException) return ex.Message;
            if (ex is HttpRequestException || ex is TaskCanceledException || ex is WebException) return "Couldn't reach the update server. Check your internet connection.";
            if (ex is UnauthorizedAccessException) return "Windows didn't let the updater replace Keystrap's files.";
            if (ex is IOException) return "Keystrap's files are in use. Close Keystrap and try again.";
            return "The update failed: " + ex.Message;
        }

        // A MajorUpdate notice for the developers' channel. Follows Keystrap's "Send crash reports" setting, and carries
        // only versions and the Windows version.
        async Task NotifyUpdatedAsync(string from, Release release)
        {
            if (!ReportsAllowed()) { Log.Write("Update notice not sent: crash reports are off"); return; }
            var n = new Notification(NotificationKind.MajorUpdate, "Keystrap " + release.Version,
                    "An installed copy of Keystrap updated itself.")
                .Field(release.Version + " Update", "Installed over " + from + " from " + release.AssetName + ".")
                .Field("Windows", Environment.OSVersion.Version + (Environment.Is64BitOperatingSystem ? " 64-bit" : " 32-bit"));
            bool ok = await WebhookService.SendAsync(n, TimeSpan.FromSeconds(5));
            Log.Write(ok ? "Update notice sent" : "Update notice couldn't be sent: " + WebhookService.LastError);
        }

        // settings.json is the page's saved state; crash reports are on unless settings.crashReports is false.
        static bool ReportsAllowed()
        {
            try
            {
                string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Keystrap", "settings.json");
                if (!File.Exists(file)) return true;
                var state = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
                object settings, on;
                if (state != null && state.TryGetValue("settings", out settings) && settings is Dictionary<string, object>
                    && ((Dictionary<string, object>)settings).TryGetValue("crashReports", out on) && on is bool)
                    return (bool)on;
            }
            catch (Exception ex) { Log.Write("Couldn't read settings.json: " + ex.Message); }
            return true;
        }

        async Task FinishAsync(int delayMs)
        {
            busy = false;
            if (!options.NoLaunch && !KeystrapRunning()) LaunchKeystrap();
            await Task.Delay(delayMs);
            Close();
        }

        void LaunchKeystrap()
        {
            try
            {
                if (!File.Exists(KeystrapExe)) return;
                Process.Start(new ProcessStartInfo(KeystrapExe) { UseShellExecute = true, WorkingDirectory = options.Dir });
                Log.Write("Started Keystrap");
            }
            catch (Exception ex) { Log.Write("Couldn't start Keystrap: " + ex.Message); }
        }

        static HttpClient NewClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Config.UserAgent);   // GitHub's API requires one
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }

        static bool IsUrl(string s) { return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase); }

        static readonly System.Text.RegularExpressions.Regex VersionTag = new System.Text.RegularExpressions.Regex(@"^[vV]?\d+(\.\d+)*$");

        // Reads GitHub's release list (or one release, or a list, in the same shape from --manifest) and picks the
        // highest version that is a real release: a tag like v1.1, not a draft or pre-release, with Keystrap.zip or
        // Keystrap.exe attached. Anything else on the releases page (a test release, a tag like "Key") is ignored.
        async Task<Release> GetLatestAsync()
        {
            string source = options.Manifest;
            if (string.IsNullOrEmpty(source))
            {
                if (string.IsNullOrWhiteSpace(options.Repo))
                    throw new UpdateException("No update source is set up yet. Updates arrive once Keystrap has a releases page.");
                source = "https://api.github.com/repos/" + options.Repo.Trim('/') + "/releases?per_page=30";
            }
            string text;
            if (IsUrl(source))
            {
                using (var http = NewClient())
                using (var resp = await http.GetAsync(source))
                {
                    if (resp.StatusCode == HttpStatusCode.NotFound) throw new UpdateException("No Keystrap release has been published yet.");
                    resp.EnsureSuccessStatusCode();
                    text = await resp.Content.ReadAsStringAsync();
                }
            }
            else text = File.ReadAllText(source);

            object parsed = new JavaScriptSerializer().DeserializeObject(text);
            IEnumerable<Dictionary<string, object>> entries = parsed is object[]
                ? ((object[])parsed).OfType<Dictionary<string, object>>()
                : new[] { parsed as Dictionary<string, object> }.Where(d => d != null);

            Release best = null;
            foreach (var json in entries)
            {
                var r = ReadRelease(json, source);
                if (r == null) continue;
                if (best == null || Versions.Compare(r.Version, best.Version) > 0) best = r;
            }
            if (best == null) throw new UpdateException("No Keystrap release has been published yet.");
            Log.Write("Latest release " + best.Version + " (" + best.AssetName + ")");
            return best;
        }

        // One release, or null when it isn't one the updater should install.
        static Release ReadRelease(Dictionary<string, object> json, string source)
        {
            var r = new Release { Version = json.ContainsKey("tag_name") ? json["tag_name"] as string : null };
            if (string.IsNullOrEmpty(r.Version) || !VersionTag.IsMatch(r.Version.Trim())) return null;
            if (true.Equals(json.ContainsKey("draft") ? json["draft"] : null) || true.Equals(json.ContainsKey("prerelease") ? json["prerelease"] : null)) return null;
            // DeserializeObject gives object[] for arrays, Deserialize<T> gives ArrayList; both are IEnumerable.
            var assets = (json.ContainsKey("assets") ? json["assets"] as IEnumerable : null) ?? new object[0];
            foreach (string wanted in new[] { "Keystrap.zip", "Keystrap.exe" })
            {
                var a = assets.OfType<Dictionary<string, object>>().FirstOrDefault(x => string.Equals(x["name"] as string, wanted, StringComparison.OrdinalIgnoreCase));
                if (a == null) continue;
                r.AssetName = wanted;
                r.AssetSource = a["browser_download_url"] as string;
                if (r.AssetSource != null && !IsUrl(r.AssetSource) && !System.IO.Path.IsPathRooted(r.AssetSource) && !IsUrl(source))
                    r.AssetSource = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(source)), r.AssetSource);
                r.Size = a.ContainsKey("size") ? Convert.ToInt64(a["size"]) : 0;
                break;
            }
            return r.AssetSource == null ? null : r;
        }

        async Task<string> DownloadAsync(Release r)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KeystrapUpdate", r.Version);
            Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, r.AssetName);
            Status("Downloading Keystrap " + r.Version + "…");
            Progress(0);
            if (!IsUrl(r.AssetSource))
            {
                File.Copy(r.AssetSource, file, true);
                Progress(0.9);
                return file;
            }
            using (var http = NewClient())
            using (var resp = await http.GetAsync(r.AssetSource, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? r.Size;
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var dst = File.Create(file))
                {
                    var buf = new byte[81920];
                    long read = 0;
                    int n, lastPct = -1;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n);
                        read += n;
                        int pct = total > 0 ? (int)(read * 100 / total) : 0;
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            Progress(0.9 * read / Math.Max(1, total));
                            status.Text = "Downloading Keystrap " + r.Version + "… " + (read / 1048576.0).ToString("0.0") + " MB" + (total > 0 ? " of " + (total / 1048576.0).ToString("0.0") + " MB" : "");
                        }
                    }
                }
            }
            Log.Write("Downloaded " + file);
            return file;
        }

        // Asks Keystrap to close the way the X button does; only forces it if it doesn't respond.
        async Task CloseKeystrapAsync()
        {
            List<Process> running = KeystrapProcesses();
            keystrapWasRunning = keystrapWasRunning || running.Count > 0;
            foreach (Process p in running)
            {
                using (p)
                {
                    try
                    {
                        p.CloseMainWindow();
                        Process proc = p;
                        if (!await Task.Run(() => proc.WaitForExit(8000)))
                        {
                            Log.Write("Keystrap didn't close in time; ending it");
                            p.Kill();
                            await Task.Run(() => proc.WaitForExit(5000));
                        }
                    }
                    catch (InvalidOperationException) { }   // already gone
                }
            }
        }

        // Puts the new files in place. Every file it replaces is first renamed to *.old, and all of them are put back
        // if anything fails, so Keystrap is never left half-updated.
        void Install(string staged, Release r)
        {
            var files = new List<KeyValuePair<string, string>>();   // source -> destination
            string temp = null;
            if (r.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(staged), "unpacked");
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
                ZipFile.ExtractToDirectory(staged, temp);
                // Accept a zip with the files at the top level or inside a single folder.
                string top = temp;
                string[] dirs = Directory.GetDirectories(temp);
                if (!File.Exists(System.IO.Path.Combine(temp, "Keystrap.exe")) && dirs.Length == 1 && File.Exists(System.IO.Path.Combine(dirs[0], "Keystrap.exe"))) top = dirs[0];
                if (!File.Exists(System.IO.Path.Combine(top, "Keystrap.exe"))) throw new UpdateException("The downloaded update doesn't contain Keystrap.exe.");
                foreach (string f in Directory.GetFiles(top, "*", SearchOption.AllDirectories))
                    files.Add(new KeyValuePair<string, string>(f, System.IO.Path.Combine(options.Dir, f.Substring(top.Length + 1))));
            }
            else files.Add(new KeyValuePair<string, string>(staged, KeystrapExe));

            // Never install something that isn't Keystrap.
            string newExe = files.First(f => string.Equals(System.IO.Path.GetFileName(f.Value), "Keystrap.exe", StringComparison.OrdinalIgnoreCase)).Key;
            var info = FileVersionInfo.GetVersionInfo(newExe);
            if (info.ProductName != "Keystrap") throw new UpdateException("The downloaded file isn't Keystrap, so it wasn't installed.");
            Log.Write("Installing Keystrap " + info.ProductVersion + " (" + files.Count + " file(s))");

            var replaced = new List<string>();
            var added = new List<string>();
            try
            {
                foreach (var f in files)
                {
                    string dest = f.Value, backup = dest + ".old";
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
                    Retry(() =>
                    {
                        if (File.Exists(backup)) File.Delete(backup);
                        if (File.Exists(dest)) { File.Move(dest, backup); replaced.Add(dest); } else added.Add(dest);
                    });
                    Retry(() => File.Copy(f.Key, dest, true));
                }
            }
            catch (Exception)
            {
                Log.Write("Install failed; restoring the previous version");
                foreach (string dest in added) { try { File.Delete(dest); } catch { } }
                foreach (string dest in replaced) { try { if (File.Exists(dest)) File.Delete(dest); File.Move(dest + ".old", dest); } catch { } }
                throw;
            }
            foreach (string dest in replaced) { try { File.Delete(dest + ".old"); } catch { } }   // a locked .old is cleaned up next time
            try { if (temp != null) Directory.Delete(temp, true); File.Delete(staged); } catch { }
            Log.Write("Installed");
        }

        // A file Keystrap just let go of can stay locked for a moment (antivirus, Windows Search); try a few times.
        static void Retry(Action action)
        {
            for (int attempt = 1; ; attempt++)
            {
                try { action(); return; }
                catch (IOException) { if (attempt >= 10) throw; System.Threading.Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { if (attempt >= 10) throw; System.Threading.Thread.Sleep(300); }
            }
        }
    }

    class UpdateException : Exception
    {
        public UpdateException(string message) : base(message) { }
    }
}
