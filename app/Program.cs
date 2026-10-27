// Keystrap desktop app: a borderless WinForms window hosting keystrap.html in WebView2, plus the
// backend the page drives through chrome.webview messages: saved state, the Roblox installer,
// launching, refresh rate, logs, the Support page's suggestion box and the Playtime tracker.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ComTypes = System.Runtime.InteropServices.ComTypes;

[assembly: AssemblyTitle("Keystrap")]
[assembly: AssemblyProduct("Keystrap")]
// The version attributes come from version.txt, through Version.g.cs (written by build.ps1).

namespace Keystrap
{
    static class AppInfo
    {
        // The release name used everywhere: logs, the suggestion box, web requests and the data files. Set it in
        // version.txt; the build carries it here and into APP_VERSION in keystrap.html.
        public const string Version = Release.Version;
        public const string UserAgent = "Keystrap/" + Version;
    }

    static class Paths
    {
        public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Keystrap");
        public static readonly string Logs = Path.Combine(Data, "Logs");
        public static readonly string Versions = Path.Combine(Data, "Versions");
        public static readonly string Downloads = Path.Combine(Data, "Downloads");
        public static readonly string State = Path.Combine(Data, "settings.json");
    }

    static class Log
    {
        static readonly object gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Paths.Logs);
                    File.AppendAllText(Path.Combine(Paths.Logs, "keystrap-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    // The WebView2 DLLs are built into Keystrap.exe, so the exe works on its own even when it's copied without them.
    // Copies next to the exe still win: the runtime only asks here when it can't find one.
    static class EmbeddedLibs
    {
        static readonly Dictionary<string, Assembly> loaded = new Dictionary<string, Assembly>();

        public static void Install()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string name = new AssemblyName(e.Name).Name;
                lock (loaded)
                {
                    Assembly a;
                    if (loaded.TryGetValue(name, out a)) return a;
                    using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("lib." + name + ".dll"))
                    {
                        if (res == null) return null;
                        var buf = new MemoryStream();
                        res.CopyTo(buf);
                        a = Assembly.Load(buf.ToArray());
                    }
                    loaded[name] = a;
                    return a;
                }
            };
        }

        // WebView2Loader.dll is native, so it has to be a real file. Without one beside the exe, unpack the built-in
        // copy into the data folder and point WebView2 at it. Must run before the first WebView2 environment is made.
        public static void UseLoader()
        {
            if (File.Exists(Path.Combine(Application.StartupPath, "WebView2Loader.dll"))) return;
            string dir = Path.Combine(Paths.Data, "Lib", AppInfo.Version);
            string file = Path.Combine(dir, "WebView2Loader.dll");
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("lib.WebView2Loader.dll"))
            {
                if (res == null) return;
                if (!File.Exists(file) || new FileInfo(file).Length != res.Length)
                {
                    Directory.CreateDirectory(dir);
                    string temp = file + ".tmp";
                    using (var f = File.Create(temp)) res.CopyTo(f);
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(temp, file);
                }
            }
            CoreWebView2Environment.SetLoaderDllFolderPath(dir);
        }
    }

    static class Program
    {
        // Keystrap.exe --dev: shows developer tools (Settings → Test notifications).
        public static bool DevMode;

        [STAThread]
        static void Main(string[] args)
        {
            EmbeddedLibs.Install();   // first, before anything touches WebView2
            WebhookService.Source = "Keystrap " + AppInfo.Version;
            DevMode = args.Any(a => string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase));
            // A Play button on roblox.com starts Keystrap.exe "roblox-player:1+launchmode:play+..." (see Protocol).
            string link = args.FirstOrDefault(RobloxLink.IsLink);

            // One Keystrap per user: a second start hands its link (or just "show yourself") to the running window.
            bool first;
            using (var single = new Mutex(true, LinkPipe.Name, out first))
            {
                // Started by the crash dialog's Restart button: wait for the crashed Keystrap to finish closing
                // instead of handing off to it.
                if (!first && args.Contains("--restart"))
                {
                    try { first = single.WaitOne(15000); }
                    catch (AbandonedMutexException) { first = true; }
                }
                if (!first)
                {
                    if (LinkPipe.Send(link ?? LinkPipe.Activate)) return;
                    Log.Write("Couldn't reach the running Keystrap; opening another window");
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // Every way an error can escape: the UI thread (including async void handlers), any other thread,
                // and faulted tasks nobody awaited. See CrashReporter.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => CrashReporter.Fatal(e.Exception, "UI thread");
                AppDomain.CurrentDomain.UnhandledException += (s, e) => CrashReporter.Fatal(e.ExceptionObject as Exception, "Background thread");
                TaskScheduler.UnobservedTaskException += (s, e) => { e.SetObserved(); CrashReporter.NonFatal(e.Exception, "Unobserved task"); };
                // .NET Framework only offers old TLS versions by default; Roblox's and Discord's APIs need TLS 1.2.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                Log.Write("Keystrap " + AppInfo.Version + " starting" + (link != null ? " from a Roblox link (" + RobloxLink.Describe(link) + ")" : ""));
                Application.Run(new MainForm(link));
            }
        }
    }

    // Crash reporting: an error nothing else caught is logged, sent to the developers' Discord (unless the user turned
    // crash reports off in Settings), and explained in a dark crash dialog; then Keystrap closes. Only these crashes are
    // reported: errors Keystrap survives are just logged, and a missing WebView2 file gets a how-to-fix message instead.
    // Reports carry the error, stack trace, Keystrap version, active profile name, Windows version and time. Paths are
    // masked so the Windows user name doesn't leave the PC.
    static class CrashReporter
    {
        static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(6);

        public static volatile bool Enabled = true;      // Settings → Send crash reports
        public static volatile string Profile;           // active profile name, kept up to date by the page
        static int fatalHandled;

        // Runs synchronously on the thread that crashed: the report is posted (or times out) before the dialog opens,
        // and the process only exits after both, so the HTTP request always has the chance to finish.
        public static void Fatal(Exception ex, string where)
        {
            if (Interlocked.Exchange(ref fatalHandled, 1) == 1) return;   // one crash report and dialog per run
            try
            {
                Log.Write("Crash (" + where + "): " + ex);
                string missing = MissingWebView2File(ex);
                if (missing != null)
                {
                    // Not a bug in Keystrap: a file it needs is missing or blocked on this PC. Say how to fix it, report nothing.
                    MessageBox.Show("Keystrap couldn't load " + missing + ", a file it needs to run.\r\n\r\n" +
                            "Download Keystrap again and unzip the whole folder, keeping its files together. If your antivirus " +
                            "removed the file, restore it or allow Keystrap, then start it again.",
                        "Keystrap", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                bool sent = Enabled && Send(ex, where, true);
                ShowDialog(ex, sent);
            }
            catch (Exception handlerError) { Log.Write("Crash handler failed: " + handlerError); }
            finally { Environment.Exit(1); }
        }

        // Faulted tasks nobody awaited: Keystrap keeps running, so this is only logged, never reported.
        public static void NonFatal(Exception ex, string where)
        {
            Log.Write("Unobserved error (" + where + "): " + ex);
        }

        // The WebView2 file behind a load failure ("Microsoft.Web.WebView2.WinForms"), or null for any other error.
        static string MissingWebView2File(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                string file = e is FileNotFoundException ? ((FileNotFoundException)e).FileName
                    : e is FileLoadException ? ((FileLoadException)e).FileName
                    : e is BadImageFormatException ? ((BadImageFormatException)e).FileName
                    : e is DllNotFoundException ? e.Message
                    : null;
                if (file == null || file.IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (e is DllNotFoundException) return "WebView2Loader.dll";
                int comma = file.IndexOf(',');
                return comma < 0 ? Path.GetFileName(file) : file.Substring(0, comma);
            }
            return null;
        }

        // Replaces the user's folder and name with placeholders.
        public static string Mask(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)) text = Regex.Replace(text, Regex.Escape(home), "%USERPROFILE%", RegexOptions.IgnoreCase);
            string user = Environment.UserName;
            if (!string.IsNullOrEmpty(user) && user.Length > 2) text = Regex.Replace(text, @"\b" + Regex.Escape(user) + @"\b", "<user>", RegexOptions.IgnoreCase);
            return text;
        }

        // A crash that closed Keystrap is a MajorCrash; an error it survived is a MinorCrash.
        public static Notification BuildNotification(Exception ex, string where, bool fatal)
        {
            Exception root = ex is AggregateException && ((AggregateException)ex).InnerExceptions.Count == 1 ? ((AggregateException)ex).InnerException : ex;
            if (root == null) root = new Exception("Unknown error (no exception object)");
            long unix = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return new Notification(fatal ? NotificationKind.MajorCrash : NotificationKind.MinorCrash,
                    (fatal ? "Crash" : "Error") + " in " + AppInfo.Version + ": " + root.GetType().Name,
                    WebhookService.Clip(Mask(root.Message).Replace("*", "\\*"), 500))
                .Field("Where", where + (fatal ? ", Keystrap closed" : ", Keystrap kept running"))
                .Field("Profile", WebhookService.Clip(string.IsNullOrEmpty(Profile) ? "unknown" : Mask(Profile), 100))
                .Field("Windows", Environment.OSVersion.Version + (Environment.Is64BitOperatingSystem ? " 64-bit" : " 32-bit"))
                .Field("Time", "<t:" + unix + ":f>")
                .Block(Mask(root.ToString()));
        }

        // Posts the report and waits at most a few seconds, so a crash never hangs on a slow network.
        static bool Send(Exception ex, string where, bool fatal)
        {
            try
            {
                bool ok = WebhookService.Send(BuildNotification(ex, where, fatal), SendTimeout);
                Log.Write(ok ? "Crash report sent" : "Crash report couldn't be sent: " + (WebhookService.LastError ?? "timed out"));
                return ok;
            }
            catch (Exception sendError)
            {
                Log.Write("Crash report couldn't be sent: " + sendError.Message);
                return false;
            }
        }

        // The dialog needs a UI thread of its own: a crash on a background thread has none.
        static void ShowDialog(Exception ex, bool sent)
        {
            var t = new Thread(() =>
            {
                try { using (var dlg = new CrashDialog(ex, sent, Enabled)) dlg.ShowDialog(); }
                catch (Exception dialogError) { Log.Write("Crash dialog failed: " + dialogError.Message); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
        }
    }

    // A small dark window in Keystrap's style, drawn with WinForms rather than WebView2 so it works whatever broke.
    class CrashDialog : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        static readonly Color Bg = Color.FromArgb(0x1c, 0x1c, 0x1f), Card = Color.FromArgb(0x26, 0x26, 0x2a), Fg = Color.FromArgb(0xf2, 0xf2, 0xf4),
            Fg2 = Color.FromArgb(0xb4, 0xb4, 0xbc), Fg3 = Color.FromArgb(0x7c, 0x7c, 0x86), Accent = Color.FromArgb(0xff, 0xfb, 0xa6), OnAccent = Color.FromArgb(0x1f, 0x1d, 0x10);

        public CrashDialog(Exception ex, bool sent, bool reportsOn)
        {
            Text = "Keystrap";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            TopMost = true;
            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 10f);
            ClientSize = new Size(460, 268);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var title = new Label { Text = "Keystrap ran into a problem", Font = new Font("Segoe UI Semibold", 15f), ForeColor = Fg, AutoSize = true, Location = new Point(24, 22) };
            var body = new Label
            {
                Text = "Something unexpected went wrong, so Keystrap needs to close. " +
                       (sent ? "A crash report was sent to the developers so they can fix it."
                             : reportsOn ? "Keystrap couldn't send a crash report, but the details are saved in its logs."
                                         : "Crash reports are turned off, so nothing was sent. The details are saved in Keystrap's logs."),
                ForeColor = Fg2, Location = new Point(24, 62), Size = new Size(412, 66),
            };
            string msg = ex == null ? "Unknown error" : CrashReporter.Mask(ex.Message);
            var detail = new Label
            {
                Text = (ex == null ? "Error" : ex.GetType().Name) + ": " + (msg.Length > 160 ? msg.Substring(0, 159) + "…" : msg),
                ForeColor = Fg3, BackColor = Card, Font = new Font("Consolas", 9f), Location = new Point(24, 136), Size = new Size(412, 54),
                Padding = new Padding(10, 8, 10, 8),
            };
            var restart = MakeButton("Restart Keystrap", true, new Point(272, 210));
            var close = MakeButton("Close", false, new Point(176, 210));
            close.Width = 88;
            var logs = new LinkLabel { Text = "Open logs folder", LinkColor = Fg2, ActiveLinkColor = Fg, AutoSize = true, Location = new Point(24, 218), LinkBehavior = LinkBehavior.HoverUnderline };

            restart.Click += (s, e) => { try { Process.Start(Application.ExecutablePath, "--restart"); } catch { } Close(); };
            close.Click += (s, e) => Close();
            logs.LinkClicked += (s, e) => { try { Directory.CreateDirectory(Paths.Logs); Process.Start("explorer.exe", "\"" + Paths.Logs + "\""); } catch { } };
            Controls.AddRange(new Control[] { title, body, detail, logs, close, restart });
            AcceptButton = restart;
            CancelButton = close;
        }

        static Button MakeButton(string text, bool primary, Point at)
        {
            var b = new Button
            {
                Text = text, Location = at, Size = new Size(164, 36), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = primary ? Accent : Card, ForeColor = primary ? OnAccent : Fg,
                Font = new Font("Segoe UI Semibold", 10f),
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(0x34, 0x34, 0x3a);
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0xff, 0xfd, 0xc8) : Color.FromArgb(0x2e, 0x2e, 0x33);
            return b;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));   // dark title bar
        }
    }

    // Custom Skies: Roblox's default skybox is six DDS textures (DXT1, 1024px, 11 mipmaps) named sky512_<face>.tex in
    // PlatformContent\pc\textures\sky of each version folder. Keystrap writes skies in exactly that format, backs up
    // the originals once per version, and copies the chosen sky over them on every launch.
    static class Skies
    {
        public const string Default = "default";
        public static readonly string Dir = Path.Combine(Paths.Data, "Skies");
        static readonly string[] Faces = { "bk", "dn", "ft", "lf", "rt", "up" };
        const int FaceSize = 1024;

        struct Rgb
        {
            public double R, G, B;
            public Rgb(double r, double g, double b) { R = r; G = g; B = b; }
            public static Rgb Hex(int hex) { return new Rgb((hex >> 16 & 255) / 255.0, (hex >> 8 & 255) / 255.0, (hex & 255) / 255.0); }
            public static Rgb Mix(Rgb a, Rgb b, double t) { t = Clamp(t); return new Rgb(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t); }
            public Rgb Add(Rgb o, double k) { return new Rgb(R + o.R * k, G + o.G * k, B + o.B * k); }
        }

        // A preset is a colour for every view direction (x right, y up, z forward; normalised), so the six faces
        // join without seams. star is a per-pixel random number for scattering stars.
        delegate Rgb SkyFn(double x, double y, double z, double star);

        class Preset
        {
            public string Id, Name, Desc;
            public SkyFn Fn;
        }

        static readonly Preset[] Presets =
        {
            new Preset { Id = "galaxy-night", Name = "Galaxy Night", Desc = "Deep violet night with a dense star field.", Fn = GalaxyNight },
            new Preset { Id = "anime-sunset", Name = "Anime Sunset", Desc = "Soft banded pinks and oranges with a low, glowing sun.", Fn = AnimeSunset },
            new Preset { Id = "blue-sky", Name = "Realistic Blue Sky", Desc = "Clear daytime sky with a hazy horizon and a bright sun.", Fn = BlueSky },
            new Preset { Id = "vaporwave", Name = "Vaporwave Synth", Desc = "Striped retro sun over a neon grid.", Fn = Vaporwave },
            new Preset { Id = "void-black", Name = "Void Black", Desc = "Pure black in every direction.", Fn = (x, y, z, s) => new Rgb(0, 0, 0) },
        };

        static double Clamp(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }
        static double Smooth(double a, double b, double v) { double t = Clamp((v - a) / (b - a)); return t * t * (3 - 2 * t); }

        // Picks a colour from a gradient of (position, colour) stops.
        static Rgb Ramp(double t, double[] at, int[] hex)
        {
            if (t <= at[0]) return Rgb.Hex(hex[0]);
            for (int i = 1; i < at.Length; i++)
                if (t <= at[i]) return Rgb.Mix(Rgb.Hex(hex[i - 1]), Rgb.Hex(hex[i]), (t - at[i - 1]) / (at[i] - at[i - 1]));
            return Rgb.Hex(hex[hex.Length - 1]);
        }

        static double Hash(double x, double y, double z)
        {
            double h = Math.Sin(x * 127.1 + y * 311.7 + z * 74.7) * 43758.5453;
            return h - Math.Floor(h);
        }

        // Smooth 3D value noise in 0..1.
        static double Noise(double x, double y, double z)
        {
            double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
            double tx = x - fx, ty = y - fy, tz = z - fz;
            tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty); tz = tz * tz * (3 - 2 * tz);
            Func<double, double, double, double> h = (a, b, c) => Hash(fx + a, fy + b, fz + c);
            double x00 = h(0, 0, 0) + (h(1, 0, 0) - h(0, 0, 0)) * tx, x10 = h(0, 1, 0) + (h(1, 1, 0) - h(0, 1, 0)) * tx;
            double x01 = h(0, 0, 1) + (h(1, 0, 1) - h(0, 0, 1)) * tx, x11 = h(0, 1, 1) + (h(1, 1, 1) - h(0, 1, 1)) * tx;
            double y0 = x00 + (x10 - x00) * ty, y1 = x01 + (x11 - x01) * ty;
            return y0 + (y1 - y0) * tz;
        }

        static double Angle(double x, double y, double z, double ax, double ay, double az)
        {
            double len = Math.Sqrt(ax * ax + ay * ay + az * az);
            return Math.Acos(Math.Max(-1, Math.Min(1, (x * ax + y * ay + z * az) / len)));
        }

        static Rgb GalaxyNight(double x, double y, double z, double star)
        {
            Rgb c = y >= 0
                ? Ramp(y, new[] { 0.0, 0.18, 0.5, 1.0 }, new[] { 0x2a1d5c, 0x1b1342, 0x0d0a26, 0x05040f })
                : Ramp(-y, new[] { 0.0, 0.3 }, new[] { 0x1b1342, 0x07060f });
            double band = Smooth(0.0, 0.18, y) * (1 - Smooth(0.2, 0.55, y));
            double neb = Noise(x * 3.2 + 7, y * 3.2, z * 3.2) * 0.65 + Noise(x * 7, y * 7, z * 7 + 3) * 0.35;
            c = c.Add(Rgb.Hex(0x7a3fb0), band * Smooth(0.35, 0.85, neb) * 0.55);
            if (y > -0.05 && star > 0.9965)
            {
                double b = (star - 0.9965) / 0.0035;
                c = c.Add(new Rgb(0.9, 0.92, 1.0), 0.35 + 0.65 * b);
            }
            return c;
        }

        static Rgb AnimeSunset(double x, double y, double z, double star)
        {
            // Posterised into soft bands for a hand-painted look.
            double t = Math.Max(0, y);
            double banded = Math.Floor(t * 14) / 14 + Smooth(0.6, 1.0, t * 14 - Math.Floor(t * 14)) / 14;
            Rgb c = y >= 0
                ? Ramp(banded, new[] { 0.0, 0.08, 0.2, 0.42, 1.0 }, new[] { 0xffd08a, 0xff9f7a, 0xe8789e, 0x7a5aa8, 0x2c2a6b })
                : Ramp(-y, new[] { 0.0, 0.25 }, new[] { 0xc77a8a, 0x3a2a4a });
            double a = Angle(x, y, z, 0, 0.07, 1);
            c = c.Add(Rgb.Hex(0xffc58a), Math.Exp(-a * a / 0.03) * 0.55);
            if (a < 0.11) c = Rgb.Mix(c, Rgb.Hex(0xfff3c9), Smooth(0.11, 0.095, a));
            return c;
        }

        static Rgb BlueSky(double x, double y, double z, double star)
        {
            Rgb c = y >= 0
                ? Ramp(y, new[] { 0.0, 0.06, 0.25, 0.6, 1.0 }, new[] { 0xe6f1fb, 0xbfe0ff, 0x7fb4ec, 0x4a88d8, 0x2a63bd })
                : Ramp(-y, new[] { 0.0, 0.08, 0.4 }, new[] { 0xd6e2ee, 0xa9b8c7, 0x7b8796 });
            double a = Angle(x, y, z, 0.15, 0.32, 0.94);
            c = c.Add(new Rgb(1, 0.97, 0.9), Math.Exp(-a * a / 0.01) * 0.6);
            if (a < 0.03) c = new Rgb(1, 1, 0.97);
            return c;
        }

        static Rgb Vaporwave(double x, double y, double z, double star)
        {
            if (y >= 0)
            {
                Rgb c = Ramp(y, new[] { 0.0, 0.1, 0.35, 0.7, 1.0 }, new[] { 0xffb86b, 0xff4fa3, 0x8a2bb8, 0x3a1170, 0x150730 });
                if (star > 0.998 && y > 0.25) c = c.Add(new Rgb(1, 0.85, 1), 0.6);
                // Striped retro sun sitting on the horizon straight ahead.
                double a = Angle(x, y, z, 0, 0.2, 1);
                if (a < 0.3 && z > 0)
                {
                    double v = (y / Math.Max(z, 0.01) - 0.2) / 0.31;   // -1 bottom .. 1 top of the sun
                    bool gap = v < 0.1 && (Math.Abs(Math.Sin((v - 0.1) * 26)) < 0.35 + (0.1 - v) * 0.6);
                    if (!gap) c = Rgb.Mix(c, Ramp((1 - v) / 2, new[] { 0.0, 1.0 }, new[] { 0xffe36b, 0xff3d8b }), Smooth(0.3, 0.29, a));
                }
                return c;
            }
            // Ground: a neon grid on the plane one unit below the camera, fading into the horizon glow.
            double t = -1 / y, gx = x * t, gz = z * t;
            double dist = Math.Sqrt(gx * gx + gz * gz);
            double line = Math.Min(Math.Abs(gx - Math.Round(gx)), Math.Abs(gz - Math.Round(gz)));
            double w = 0.03 + dist * 0.004;
            Rgb ground = Rgb.Hex(0x12051f).Add(Rgb.Hex(0x2fe6ff), Smooth(w, 0, line) * Math.Exp(-dist * 0.05));
            return Rgb.Mix(ground, Rgb.Hex(0xff4fa3), Math.Exp(y * 18) * 0.85);
        }

        // Direction for pixel (u, v) in -1..1 (v up) on each face of the cube.
        static void FaceDir(string face, double u, double v, out double x, out double y, out double z)
        {
            switch (face)
            {
                case "ft": x = u; y = v; z = 1; break;
                case "bk": x = -u; y = v; z = -1; break;
                case "rt": x = 1; y = v; z = -u; break;
                case "lf": x = -1; y = v; z = u; break;
                case "up": x = u; y = 1; z = -v; break;
                default: x = u; y = -1; z = v; break;   // dn
            }
            double len = Math.Sqrt(x * x + y * y + z * z);
            x /= len; y /= len; z /= len;
        }

        static float[] RenderFace(Preset p, string face)
        {
            var px = new float[FaceSize * FaceSize * 3];
            var rnd = new Random(face.GetHashCode() ^ p.Id.GetHashCode());
            for (int j = 0; j < FaceSize; j++)
                for (int i = 0; i < FaceSize; i++)
                {
                    double x, y, z;
                    FaceDir(face, (i + 0.5) / FaceSize * 2 - 1, 1 - (j + 0.5) / FaceSize * 2, out x, out y, out z);
                    Rgb c = p.Fn(x, y, z, rnd.NextDouble());
                    int k = (j * FaceSize + i) * 3;
                    px[k] = (float)Clamp(c.R); px[k + 1] = (float)Clamp(c.G); px[k + 2] = (float)Clamp(c.B);
                }
            return px;
        }

        // ---------- DDS / DXT1 ----------

        static ushort To565(float r, float g, float b)
        {
            return (ushort)(((int)Math.Round(Clamp(r) * 31) << 11) | ((int)Math.Round(Clamp(g) * 63) << 5) | (int)Math.Round(Clamp(b) * 31));
        }

        static void From565(ushort c, out float r, out float g, out float b)
        {
            r = ((c >> 11) & 31) / 31f; g = ((c >> 5) & 63) / 63f; b = (c & 31) / 31f;
        }

        static void EncodeBlock(float[] px, int size, int bx, int by, BinaryWriter w)
        {
            var block = new float[48];
            float[] lo = { 1, 1, 1 }, hi = { 0, 0, 0 };
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 4; i++)
                    for (int c = 0; c < 3; c++)
                    {
                        float v = px[((by * 4 + j) * size + bx * 4 + i) * 3 + c];
                        block[(j * 4 + i) * 3 + c] = v;
                        if (v < lo[c]) lo[c] = v;
                        if (v > hi[c]) hi[c] = v;
                    }
            for (int c = 0; c < 3; c++) { float inset = (hi[c] - lo[c]) / 16; lo[c] += inset; hi[c] -= inset; }
            ushort c0 = To565(hi[0], hi[1], hi[2]), c1 = To565(lo[0], lo[1], lo[2]);
            if (c0 < c1) { ushort t = c0; c0 = c1; c1 = t; }
            uint indices = 0;
            if (c0 != c1)
            {
                var pal = new float[4, 3];
                float r0, g0, b0, r1, g1, b1;
                From565(c0, out r0, out g0, out b0); From565(c1, out r1, out g1, out b1);
                float[] a = { r0, g0, b0 }, b = { r1, g1, b1 };
                for (int c = 0; c < 3; c++) { pal[0, c] = a[c]; pal[1, c] = b[c]; pal[2, c] = (2 * a[c] + b[c]) / 3; pal[3, c] = (a[c] + 2 * b[c]) / 3; }
                for (int p = 0; p < 16; p++)
                {
                    int best = 0; float bestD = float.MaxValue;
                    for (int k = 0; k < 4; k++)
                    {
                        float dr = block[p * 3] - pal[k, 0], dg = block[p * 3 + 1] - pal[k, 1], db = block[p * 3 + 2] - pal[k, 2];
                        float d = dr * dr * 0.3f + dg * dg * 0.59f + db * db * 0.11f;
                        if (d < bestD) { bestD = d; best = k; }
                    }
                    indices |= (uint)best << (p * 2);
                }
            }
            w.Write(c0); w.Write(c1); w.Write(indices);
        }

        static float[] Downsample(float[] px, int size)
        {
            int half = size / 2;
            var o = new float[half * half * 3];
            for (int j = 0; j < half; j++)
                for (int i = 0; i < half; i++)
                    for (int c = 0; c < 3; c++)
                        o[(j * half + i) * 3 + c] = (px[((2 * j) * size + 2 * i) * 3 + c] + px[((2 * j) * size + 2 * i + 1) * 3 + c] +
                                                     px[((2 * j + 1) * size + 2 * i) * 3 + c] + px[((2 * j + 1) * size + 2 * i + 1) * 3 + c]) / 4;
            return o;
        }

        // Same layout as Roblox's own sky files: DXT1 with a full mip chain down to 1x1.
        static void WriteDds(string path, float[] px, int size)
        {
            int mips = (int)Math.Log(size, 2) + 1;
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(0x20534444);                    // "DDS "
                w.Write(124);
                w.Write(0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000);   // caps, height, width, pixelformat, mipmapcount, linearsize
                w.Write(size); w.Write(size);
                w.Write(Math.Max(1, size / 4) * Math.Max(1, size / 4) * 8);
                w.Write(0); w.Write(mips);
                for (int i = 0; i < 11; i++) w.Write(0);
                w.Write(32); w.Write(0x4); w.Write(0x31545844);   // pixel format: FourCC "DXT1"
                for (int i = 0; i < 5; i++) w.Write(0);
                w.Write(0x1000 | 0x8 | 0x400000);       // texture, complex, mipmap
                for (int i = 0; i < 4; i++) w.Write(0);
                for (int s = size; s >= 1; s /= 2)
                {
                    if (s >= 4)
                    {
                        for (int by = 0; by < s / 4; by++)
                            for (int bx = 0; bx < s / 4; bx++) EncodeBlock(px, s, bx, by, w);
                    }
                    else
                    {
                        // 2x2 and 1x1 levels still take one 4x4 block: repeat the pixels to fill it.
                        var pad = new float[48];
                        for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) for (int c = 0; c < 3; c++)
                            pad[(j * 4 + i) * 3 + c] = px[((j % s) * s + (i % s)) * 3 + c];
                        EncodeBlock(pad, 4, 0, 0, w);
                    }
                    if (s > 1) px = Downsample(px, s);
                }
            }
        }

        // Reads the top level of a DXT1 .tex/.dds into a bitmap (for previews); null for other formats.
        static Bitmap ReadDds(string path)
        {
            byte[] d = File.ReadAllBytes(path);
            if (d.Length < 128 || BitConverter.ToInt32(d, 0) != 0x20534444 || BitConverter.ToInt32(d, 84) != 0x31545844) return null;
            int h = BitConverter.ToInt32(d, 12), wd = BitConverter.ToInt32(d, 16);
            var bmp = new Bitmap(wd, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, wd, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            var row = new int[wd * h];
            int o = 128;
            for (int by = 0; by < h / 4; by++)
                for (int bx = 0; bx < wd / 4; bx++, o += 8)
                {
                    ushort c0 = BitConverter.ToUInt16(d, o), c1 = BitConverter.ToUInt16(d, o + 2);
                    uint idx = BitConverter.ToUInt32(d, o + 4);
                    float r0, g0, b0, r1, g1, b1;
                    From565(c0, out r0, out g0, out b0); From565(c1, out r1, out g1, out b1);
                    var pal = new int[4];
                    Func<float, float, float, int> argb = (r, g, b) => unchecked((int)0xFF000000) | ((int)(r * 255) << 16) | ((int)(g * 255) << 8) | (int)(b * 255);
                    pal[0] = argb(r0, g0, b0); pal[1] = argb(r1, g1, b1);
                    if (c0 > c1) { pal[2] = argb((2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3); pal[3] = argb((r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3); }
                    else { pal[2] = argb((r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2); pal[3] = unchecked((int)0xFF000000); }
                    for (int p = 0; p < 16; p++) row[(by * 4 + p / 4) * wd + bx * 4 + p % 4] = pal[(idx >> (p * 2)) & 3];
                }
            Marshal.Copy(row, 0, data.Scan0, row.Length);
            bmp.UnlockBits(data);
            return bmp;
        }

        static float[] Pixels(Image img)
        {
            using (var bmp = new Bitmap(FaceSize, FaceSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(img, new Rectangle(0, 0, FaceSize, FaceSize));
                }
                var data = bmp.LockBits(new Rectangle(0, 0, FaceSize, FaceSize), System.Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
                var raw = new int[FaceSize * FaceSize];
                Marshal.Copy(data.Scan0, raw, 0, raw.Length);
                bmp.UnlockBits(data);
                var px = new float[raw.Length * 3];
                for (int i = 0; i < raw.Length; i++)
                {
                    px[i * 3] = ((raw[i] >> 16) & 255) / 255f; px[i * 3 + 1] = ((raw[i] >> 8) & 255) / 255f; px[i * 3 + 2] = (raw[i] & 255) / 255f;
                }
                return px;
            }
        }

        // ---------- previews ----------

        static string PngDataUri(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
        }

        // A 16:9 view looking at the front face, tilted slightly up.
        static Bitmap RenderPreview(Preset p)
        {
            const int W = 320, H = 180;
            var bmp = new Bitmap(W, H);
            var rnd = new Random(p.Id.GetHashCode());
            double pitch = 0.12, f = Math.Tan(0.85);
            for (int j = 0; j < H; j++)
                for (int i = 0; i < W; i++)
                {
                    double vx = ((i + 0.5) / W * 2 - 1) * f, vy = (1 - (j + 0.5) / H * 2) * f * H / W;
                    double y = vy * Math.Cos(pitch) + Math.Sin(pitch), z = -vy * Math.Sin(pitch) + Math.Cos(pitch);
                    double len = Math.Sqrt(vx * vx + y * y + z * z);
                    Rgb c = p.Fn(vx / len, y / len, z / len, rnd.NextDouble() > 0.5 ? rnd.NextDouble() : 0);
                    bmp.SetPixel(i, j, Color.FromArgb((int)(Clamp(c.R) * 255), (int)(Clamp(c.G) * 255), (int)(Clamp(c.B) * 255)));
                }
            return bmp;
        }

        // The middle band of a front face, scaled to 320x180.
        static Bitmap CropPreview(Image ft)
        {
            var bmp = new Bitmap(320, 180);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                int band = ft.Width * 9 / 16;
                g.DrawImage(ft, new Rectangle(0, 0, 320, 180), new Rectangle(0, (ft.Height - band) / 2 - ft.Height / 10, ft.Width, band), GraphicsUnit.Pixel);
            }
            return bmp;
        }

        static string PreviewOf(string id, string versionDir)
        {
            string cache = Path.Combine(Dir, id, "preview.png");
            if (File.Exists(cache)) return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(cache));
            Preset p = Presets.FirstOrDefault(x => x.Id == id);
            Bitmap bmp = null;
            try
            {
                if (p != null) bmp = RenderPreview(p);
                else if (id == Default)
                {
                    // Roblox's own sky: read it from this version (its backup, if a custom sky is applied there).
                    if (versionDir == null) return null;
                    string skyDir = SkyDir(versionDir);
                    string ft = Path.Combine(skyDir, "keystrap-original", "sky512_ft.tex");
                    if (!File.Exists(ft)) ft = Path.Combine(skyDir, "sky512_ft.tex");
                    if (!File.Exists(ft)) return null;
                    using (Bitmap face = ReadDds(ft)) if (face != null) bmp = CropPreview(face);
                }
                if (bmp == null) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(cache));
                bmp.Save(cache, System.Drawing.Imaging.ImageFormat.Png);
                return PngDataUri(bmp);
            }
            finally
            {
                if (bmp != null) bmp.Dispose();
            }
        }

        // ---------- library ----------

        // Everything the Custom Skies page lists: Roblox's default sky, the presets, then imported skies (oldest first).
        public static List<Dictionary<string, object>> List(string versionDir)
        {
            var items = new List<Dictionary<string, object>>();
            Func<string, string, string, bool, Dictionary<string, object>> item = (id, name, desc, custom) =>
                new Dictionary<string, object> { { "id", id }, { "name", name }, { "desc", desc }, { "custom", custom }, { "preview", PreviewOf(id, versionDir) } };
            items.Add(item(Default, "Roblox Default", "The sky Roblox ships with.", false));
            foreach (Preset p in Presets) items.Add(item(p.Id, p.Name, p.Desc, false));
            if (Directory.Exists(Dir))
                foreach (string d in Directory.GetDirectories(Dir, "custom-*").OrderBy(Directory.GetCreationTimeUtc))
                {
                    string name = File.Exists(Path.Combine(d, "name.txt")) ? File.ReadAllText(Path.Combine(d, "name.txt")).Trim() : "Custom sky";
                    items.Add(item(Path.GetFileName(d), name, "Imported from your files.", true));
                }
            return items;
        }

        // Folder with the six sky512_<face>.tex files for this sky; presets are rendered the first time they're needed.
        static string Ensure(string id, Action<string> progress)
        {
            string dir = Path.Combine(Dir, id);
            if (Faces.All(f => File.Exists(Path.Combine(dir, "sky512_" + f + ".tex")))) return dir;
            Preset p = Presets.FirstOrDefault(x => x.Id == id);
            if (p == null) throw new InvalidOperationException("This sky's files are missing. Import it again.");
            Directory.CreateDirectory(dir);
            foreach (string face in Faces)
            {
                progress("Rendering " + p.Name + " (" + face + ")…");
                string tmp = Path.Combine(dir, "sky512_" + face + ".tmp");
                WriteDds(tmp, RenderFace(p, face), FaceSize);
                File.Copy(tmp, Path.Combine(dir, "sky512_" + face + ".tex"), true);
                File.Delete(tmp);
            }
            Log.Write("Rendered sky preset " + p.Name);
            return dir;
        }

        static string SkyDir(string versionDir) { return Path.Combine(versionDir, "PlatformContent", "pc", "textures", "sky"); }

        // Puts a sky into a Roblox version folder. The first time, Roblox's own six files are kept in keystrap-original,
        // which is also what "default" copies back.
        public static void ApplyTo(string versionDir, string id, Action<string> progress)
        {
            string skyDir = SkyDir(versionDir);
            if (!File.Exists(Path.Combine(skyDir, "sky512_ft.tex"))) throw new InvalidOperationException("This Roblox version has no sky files to replace.");
            string backup = Path.Combine(skyDir, "keystrap-original");
            if (!Directory.Exists(backup))
            {
                if (id == Default) return;   // nothing has been replaced in this version
                Directory.CreateDirectory(backup);
                foreach (string f in Faces) File.Copy(Path.Combine(skyDir, "sky512_" + f + ".tex"), Path.Combine(backup, "sky512_" + f + ".tex"), true);
            }
            string src = id == Default ? backup : Ensure(id, progress);
            foreach (string f in Faces) File.Copy(Path.Combine(src, "sky512_" + f + ".tex"), Path.Combine(skyDir, "sky512_" + f + ".tex"), true);
            Log.Write("Applied sky " + id + " to " + versionDir);
        }

        public static void Prepare(string id, Action<string> progress)
        {
            if (id != Default) Ensure(id, progress);
        }

        static readonly Regex FaceName = new Regex(@"(?:^|[_\-\s.])(bk|dn|ft|lf|rt|up|back|down|front|left|right|top|bottom)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Dictionary<string, string> FaceAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { { "back", "bk" }, { "down", "dn" }, { "bottom", "dn" }, { "front", "ft" }, { "left", "lf" }, { "right", "rt" }, { "top", "up" } };

        // Imports six face files (PNG/JPG/BMP images, or .tex/.dds textures), named with bk/dn/ft/lf/rt/up or back/down/front/left/right/up.
        public static string Import(string[] files)
        {
            if (files.Any(f => f.EndsWith(".rbxm", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".rbxmx", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Roblox model files only point to images stored on Roblox's servers, which Keystrap can't download. Export the sky's six images and import those instead.");
            var byFace = new Dictionary<string, string>();
            foreach (string f in files)
            {
                Match m = FaceName.Match(Path.GetFileNameWithoutExtension(f));
                if (!m.Success) continue;
                string face = m.Groups[1].Value.ToLowerInvariant(), alias;
                if (FaceAlias.TryGetValue(face, out alias)) face = alias;
                byFace[face] = f;
            }
            string[] missing = Faces.Where(f => !byFace.ContainsKey(f)).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException("Choose all six sky images, named so their side is at the end, like sky_bk, sky_dn, sky_ft, sky_lf, sky_rt and sky_up (back, down, front, left, right and up also work). Missing: " + string.Join(", ", missing) + ".");

            string id = "custom-" + DateTime.UtcNow.Ticks.ToString("x");
            string dir = Path.Combine(Dir, id);
            Directory.CreateDirectory(dir);
            try
            {
                foreach (string face in Faces)
                {
                    string src = byFace[face], dest = Path.Combine(dir, "sky512_" + face + ".tex");
                    string ext = Path.GetExtension(src).ToLowerInvariant();
                    if (ext == ".tex" || ext == ".dds")
                    {
                        using (var s = File.OpenRead(src))
                        {
                            var head = new byte[4];
                            if (s.Read(head, 0, 4) != 4 || BitConverter.ToInt32(head, 0) != 0x20534444)
                                throw new InvalidOperationException(Path.GetFileName(src) + " isn't a DDS texture.");
                        }
                        File.Copy(src, dest, true);
                    }
                    else
                    {
                        using (Image img = Image.FromFile(src)) WriteDds(dest, Pixels(img), FaceSize);
                    }
                }
                string ftSrc = byFace["ft"];
                using (Image ft = ftSrc.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) || ftSrc.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                    ? (Image)ReadDds(Path.Combine(dir, "sky512_ft.tex")) : Image.FromFile(ftSrc))
                    if (ft != null) using (Bitmap prev = CropPreview(ft)) prev.Save(Path.Combine(dir, "preview.png"), System.Drawing.Imaging.ImageFormat.Png);

                string name = Regex.Replace(FaceName.Replace(Path.GetFileNameWithoutExtension(byFace["ft"]), ""), @"[_\-\s.]+$", "").Trim();
                File.WriteAllText(Path.Combine(dir, "name.txt"), name.Length > 0 ? name : "Custom sky");
                Log.Write("Imported custom sky " + id + " from " + Path.GetDirectoryName(byFace["ft"]));
                return id;
            }
            catch (OutOfMemoryException)
            {
                Directory.Delete(dir, true);
                throw new InvalidOperationException("One of those files isn't an image Keystrap can read. Use PNG, JPG or BMP.");
            }
            catch
            {
                Directory.Delete(dir, true);
                throw;
            }
        }

        public static void Delete(string id)
        {
            if (!id.StartsWith("custom-")) throw new InvalidOperationException("Only imported skies can be deleted.");
            string dir = Path.Combine(Dir, id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Log.Write("Deleted custom sky " + id);
        }
    }

    // Discord Rich Presence over Discord's local IPC pipe (\\.\pipe\discord-ipc-0..9): a handshake with the application
    // ID, then SET_ACTIVITY commands. Frames are [int32 opcode][int32 length][UTF-8 JSON], little-endian.
    //
    // Threading rules (the first version broke these and froze Keystrap's window):
    //  - The pipe is opened for overlapped I/O, so the reader thread's pending read never blocks a write. On a
    //    synchronous pipe handle Windows queues a write behind the read, and Discord can go minutes without sending.
    //  - No pipe I/O ever runs on the UI thread: Configure, SetActivity and Dispose hand the work to a background queue.
    //  - Nothing waits for Discord without a time limit (handshake 5 s, shutdown 1 s).
    // StatusChanged is raised on whatever background thread noticed the change.
    class DiscordPresence : IDisposable
    {
        const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;

        public event Action<string, string> StatusChanged;   // state ("off", "no-id", "connecting", "connected", "no-discord", "error"), detail

        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly object gate = new object();
        readonly object writeLock = new object();
        readonly System.Threading.Timer retry;
        readonly int pid = Process.GetCurrentProcess().Id;
        NamedPipeClientStream pipe;
        bool enabled, connecting, disposed;
        string appId;
        Dictionary<string, object> activity;   // what should be shown; re-sent after every reconnect
        string lastState;
        Task queue = Task.FromResult(0);       // pipe work requested from the UI thread, run one at a time in order

        public DiscordPresence()
        {
            // Discord may start after Keystrap, or restart: try again every 15 seconds while enabled.
            retry = new System.Threading.Timer(_ => TryConnect(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public static bool ValidAppId(string id) { return id != null && Regex.IsMatch(id, @"^\d{17,20}$"); }

        // Runs pipe work on a background thread, after any work already queued. Never throws.
        void Enqueue(Action work)
        {
            lock (gate)
                queue = queue.ContinueWith(_ =>
                {
                    try { work(); }
                    catch (Exception ex) { Log.Write("Discord presence: " + ex.Message); }
                }, TaskScheduler.Default);
        }

        public void Configure(bool on, string id)
        {
            NamedPipeClientStream old;
            lock (gate)
            {
                if (disposed) return;
                bool changed = on != enabled || id != appId;
                enabled = on;
                appId = id;
                if (!changed) return;
                old = pipe;
                pipe = null;
            }
            Enqueue(() => CloseQuietly(old));
            if (!on) { Report("off", null); retry.Change(Timeout.Infinite, Timeout.Infinite); return; }
            if (!ValidAppId(id)) { Report("no-id", null); retry.Change(Timeout.Infinite, Timeout.Infinite); return; }
            retry.Change(0, 15000);
        }

        public void SetActivity(Dictionary<string, object> a)
        {
            lock (gate) { if (disposed) return; activity = a; }
            Enqueue(SendActivity);
        }

        void TryConnect()
        {
            string id;
            lock (gate)
            {
                if (disposed || !enabled || !ValidAppId(appId) || pipe != null || connecting) return;
                connecting = true;
                id = appId;
            }
            if (lastState != "connected") Report("connecting", null);
            NamedPipeClientStream p = null;
            try
            {
                for (int i = 0; i < 10 && p == null; i++)
                {
                    var candidate = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try { candidate.Connect(200); p = candidate; }
                    catch (Exception) { candidate.Dispose(); }
                }
                if (p == null) { Report("no-discord", null); return; }

                Write(p, OpHandshake, new Dictionary<string, object> { { "v", 1 }, { "client_id", id } });
                NamedPipeClientStream hp = p;
                var handshake = Task.Run(() => { int o; var r = Read(hp, out o); return Tuple.Create(o, r); });
                // If we give up waiting, the read fails later when the pipe closes; observe it so it isn't reported as a crash.
                handshake.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                if (!handshake.Wait(5000)) throw new TimeoutException("Discord didn't answer the handshake");
                int op = handshake.Result.Item1;
                Dictionary<string, object> reply = handshake.Result.Item2;
                if (op == OpClose || reply == null || (reply.ContainsKey("evt") && reply["evt"] as string == "ERROR"))
                {
                    string msg = reply != null && reply.ContainsKey("message") ? reply["message"] as string : "Discord closed the connection.";
                    Log.Write("Discord rejected the handshake: " + msg);
                    CloseQuietly(p, false);
                    p = null;
                    retry.Change(Timeout.Infinite, Timeout.Infinite);   // a wrong ID won't fix itself; wait for a new one
                    Report("error", msg);
                    return;
                }
                string user = null;
                var data = reply.ContainsKey("data") ? reply["data"] as Dictionary<string, object> : null;
                var u = data != null && data.ContainsKey("user") ? data["user"] as Dictionary<string, object> : null;
                if (u != null) user = (u.ContainsKey("global_name") ? u["global_name"] as string : null) ?? (u.ContainsKey("username") ? u["username"] as string : null);

                // The setting may have changed (or Keystrap started closing) while we were connecting.
                bool stale;
                lock (gate)
                {
                    stale = disposed || !enabled || appId != id;
                    if (!stale) pipe = p;
                }
                if (stale) { CloseQuietly(p, false); p = null; return; }

                Log.Write("Connected to Discord" + (user != null ? " as " + user : ""));
                Report("connected", user);
                NamedPipeClientStream rp = p;
                var reader = new Thread(() => ReadLoop(rp)) { IsBackground = true, Name = "Discord IPC reader" };
                reader.Start();
                Enqueue(SendActivity);
            }
            catch (Exception ex)
            {
                Log.Write("Discord connection failed: " + ex.Message);
                CloseQuietly(p, false);
                Report("no-discord", null);
            }
            finally
            {
                lock (gate) connecting = false;
            }
        }

        // Keeps the pipe drained, answers pings and notices when Discord goes away.
        void ReadLoop(NamedPipeClientStream p)
        {
            try
            {
                while (true)
                {
                    int op;
                    Dictionary<string, object> msg = Read(p, out op);
                    if (op == OpPing) { Write(p, OpPong, msg); continue; }
                    if (op == OpClose) break;
                    if (msg != null && msg.ContainsKey("evt") && msg["evt"] as string == "ERROR")
                    {
                        var data = msg.ContainsKey("data") ? msg["data"] as Dictionary<string, object> : null;
                        Log.Write("Discord refused the activity: " + (data != null && data.ContainsKey("message") ? data["message"] : "unknown error"));
                    }
                }
            }
            catch (Exception) { }   // pipe closed by us or by Discord
            bool lost;
            lock (gate) { lost = pipe == p; if (lost) pipe = null; lost = lost && enabled && !disposed; }
            CloseQuietly(p, false);
            if (lost) { Log.Write("Lost the connection to Discord; retrying"); Report("no-discord", null); }
        }

        void SendActivity()
        {
            NamedPipeClientStream p;
            Dictionary<string, object> a;
            lock (gate) { p = pipe; a = activity; }
            if (p == null) return;
            try
            {
                Write(p, OpFrame, new Dictionary<string, object>
                {
                    { "cmd", "SET_ACTIVITY" },
                    { "args", new Dictionary<string, object> { { "pid", pid }, { "activity", a } } },
                    { "nonce", Guid.NewGuid().ToString() },
                });
            }
            catch (Exception ex) { Log.Write("Sending presence to Discord failed: " + ex.Message); }
        }

        // No Flush: on a pipe that waits until Discord has read the data, which is exactly the kind of wait to avoid.
        void Write(NamedPipeClientStream p, int op, object payload)
        {
            byte[] body = Encoding.UTF8.GetBytes(json.Serialize(payload));
            var frame = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(frame, 0);
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            body.CopyTo(frame, 8);
            lock (writeLock) p.Write(frame, 0, frame.Length);
        }

        Dictionary<string, object> Read(NamedPipeClientStream p, out int op)
        {
            byte[] head = ReadExactly(p, 8);
            op = BitConverter.ToInt32(head, 0);
            int len = BitConverter.ToInt32(head, 4);
            if (len < 0 || len > 1 << 20) throw new InvalidDataException("Bad frame from Discord");
            string text = Encoding.UTF8.GetString(ReadExactly(p, len));
            try { return json.Deserialize<Dictionary<string, object>>(text); }
            catch (Exception) { return null; }
        }

        static byte[] ReadExactly(Stream s, int n)
        {
            var buf = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = s.Read(buf, got, n - got);
                if (r <= 0) throw new EndOfStreamException();
                got += r;
            }
            return buf;
        }

        void Report(string state, string detail)
        {
            if (disposed) return;
            lastState = state;
            var h = StatusChanged;
            if (h == null) return;
            try { h(state, detail); }
            catch (Exception ex) { Log.Write("Discord status handler failed: " + ex.Message); }
        }

        // Clears the card (unless told not to) and closes the pipe. Safe with null, twice, or on a dead pipe.
        void CloseQuietly(NamedPipeClientStream p, bool clearCard = true)
        {
            if (p == null) return;
            if (clearCard)
            {
                try
                {
                    // Clear the card straight away rather than waiting for Discord to notice the pipe closed.
                    Write(p, OpFrame, new Dictionary<string, object>
                    {
                        { "cmd", "SET_ACTIVITY" }, { "args", new Dictionary<string, object> { { "pid", pid } } }, { "nonce", Guid.NewGuid().ToString() },
                    });
                }
                catch (Exception) { }
            }
            try { p.Dispose(); } catch (Exception) { }   // also ends the reader thread's pending read
        }

        // Called while Keystrap closes, on the UI thread: stops reconnecting and closes the pipe in the background,
        // waiting at most a second so closing the window can never hang on Discord.
        public void Dispose()
        {
            NamedPipeClientStream old;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                enabled = false;
                old = pipe;
                pipe = null;
            }
            try { retry.Dispose(); } catch (Exception) { }
            try { Task.Run(() => CloseQuietly(old)).Wait(1000); } catch (Exception) { }
        }
    }

    // Creator name and icon for an experience, from Roblox's public APIs, cached per universe.
    static class RobloxGames
    {
        public class Info { public string Creator; public string Icon; }
        static readonly Dictionary<long, Info> cache = new Dictionary<long, Info>();
        static readonly HashSet<long> loading = new HashSet<long>();
        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
            return client;
        }

        // Returns what's known now; if nothing is, starts a lookup and calls loaded (on the calling thread's context) when it arrives.
        public static Info Get(long universe, Action loaded)
        {
            if (universe <= 0) return null;
            Info info;
            if (cache.TryGetValue(universe, out info)) return info;
            if (loading.Add(universe)) Load(universe, loaded);
            return null;
        }

        static async void Load(long universe, Action loaded)
        {
            var info = new Info();
            var json = new JavaScriptSerializer();
            try
            {
                var g = json.Deserialize<Dictionary<string, object>>(await http.GetStringAsync("https://games.roblox.com/v1/games?universeIds=" + universe));
                var game = g.ContainsKey("data") && g["data"] is ArrayList && ((ArrayList)g["data"]).Count > 0 ? ((ArrayList)g["data"])[0] as Dictionary<string, object> : null;
                var creator = game != null && game.ContainsKey("creator") ? game["creator"] as Dictionary<string, object> : null;
                if (creator != null && creator.ContainsKey("name")) info.Creator = creator["name"] as string;
            }
            catch (Exception ex) { Log.Write("Couldn't look up the creator of universe " + universe + ": " + ex.Message); }
            try
            {
                var t = json.Deserialize<Dictionary<string, object>>(await http.GetStringAsync(
                    "https://thumbnails.roblox.com/v1/games/icons?universeIds=" + universe + "&size=512x512&format=Png&isCircular=false"));
                var img = t.ContainsKey("data") && t["data"] is ArrayList && ((ArrayList)t["data"]).Count > 0 ? ((ArrayList)t["data"])[0] as Dictionary<string, object> : null;
                if (img != null && img.ContainsKey("imageUrl")) info.Icon = img["imageUrl"] as string;
            }
            catch (Exception ex) { Log.Write("Couldn't look up the icon of universe " + universe + ": " + ex.Message); }
            cache[universe] = info;
            loading.Remove(universe);
            loaded();
        }
    }

    // A launch link from the Roblox website, e.g.
    // roblox-player:1+launchmode:play+gameinfo:<ticket>+launchtime:…+placelauncherurl:https%3A…+browsertrackerid:…+robloxLocale:en_us
    // Keystrap passes the link to RobloxPlayerBeta.exe unchanged; parsing is only to check it and to log what's safe to log.
    static class RobloxLink
    {
        // Parameters that carry credentials are never written to the log.
        static readonly HashSet<string> Secret = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "gameinfo" };
        static readonly Regex PlaceId = new Regex(@"[?&]placeId=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsLink(string arg)
        {
            return arg != null && (arg.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("roblox:", StringComparison.OrdinalIgnoreCase));
        }

        // roblox-player links are "+"-separated "key:value" pairs after the "roblox-player:1" header.
        public static Dictionary<string, string> Parse(string link)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!link.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase)) return result;
            foreach (string part in link.Substring("roblox-player:".Length).Split('+'))
            {
                int colon = part.IndexOf(':');
                if (colon > 0) result[part.Substring(0, colon)] = part.Substring(colon + 1);
            }
            return result;
        }

        public static string Describe(string link)
        {
            if (link.StartsWith("roblox:", StringComparison.OrdinalIgnoreCase)) return "roblox: deep link";
            var p = Parse(link);
            var parts = new List<string>();
            string v;
            if (p.TryGetValue("launchmode", out v)) parts.Add("mode " + v);
            if (p.TryGetValue("placelauncherurl", out v))
            {
                Match m = PlaceId.Match(Uri.UnescapeDataString(v));
                if (m.Success) parts.Add("place " + m.Groups[1].Value);
            }
            parts.Add("parameters: " + string.Join(", ", p.Keys.Select(k => Secret.Contains(k) ? k + " (hidden)" : k)));
            return string.Join("; ", parts);
        }
    }

    // Makes Keystrap the handler for roblox-player: and roblox: links (per user, HKCU), the way Bloxstrap does.
    // Whatever handled them before (normally Roblox's own RobloxPlayerBeta.exe) is backed up and put back by Unregister.
    static class Protocol
    {
        static readonly string[] Schemes = { "roblox-player", "roblox" };
        const string BackupKey = @"Software\Keystrap\ProtocolBackup";

        static string OurCommand() { return "\"" + Application.ExecutablePath + "\" \"%1\""; }

        public static void Register()
        {
            string ours = OurCommand();
            foreach (string scheme in Schemes)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + scheme))
                using (RegistryKey cmd = key.CreateSubKey(@"shell\open\command"))
                {
                    string current = cmd.GetValue("") as string;
                    if (current == ours) continue;
                    if (!string.IsNullOrEmpty(current) && current.IndexOf("Keystrap.exe", StringComparison.OrdinalIgnoreCase) < 0)
                        using (RegistryKey backup = Registry.CurrentUser.CreateSubKey(BackupKey))
                            backup.SetValue(scheme, current);
                    key.SetValue("", scheme == "roblox" ? "URL: Roblox Protocol" : "URL: Roblox Player Protocol");
                    key.SetValue("URL Protocol", "");
                    using (RegistryKey icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", Application.ExecutablePath + ",0");
                    cmd.SetValue("", ours);
                    Log.Write("Registered Keystrap for " + scheme + ": links (was: " + (current ?? "nothing") + ")");
                }
            }
        }

        public static void Unregister()
        {
            using (RegistryKey backup = Registry.CurrentUser.OpenSubKey(BackupKey, true))
            {
                foreach (string scheme in Schemes)
                {
                    using (RegistryKey cmd = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + scheme + @"\shell\open\command", true))
                    {
                        string current = cmd == null ? null : cmd.GetValue("") as string;
                        if (current == null || current.IndexOf("Keystrap.exe", StringComparison.OrdinalIgnoreCase) < 0) continue;   // not ours any more
                        string previous = backup == null ? null : backup.GetValue(scheme) as string;
                        if (previous != null)
                        {
                            cmd.SetValue("", previous);
                            using (RegistryKey icon = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + scheme + @"\DefaultIcon", true))
                                if (icon != null) icon.SetValue("", previous.Split('"').Where(s => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).FirstOrDefault() ?? "");
                            Log.Write("Gave " + scheme + ": links back to " + previous);
                        }
                        else
                        {
                            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + scheme, false);
                            Log.Write("Removed Keystrap's " + scheme + ": link handler");
                        }
                    }
                }
            }
        }

        public static bool IsRegistered()
        {
            using (RegistryKey cmd = Registry.CurrentUser.OpenSubKey(@"Software\Classes\roblox-player\shell\open\command"))
                return cmd != null && (cmd.GetValue("") as string) == OurCommand();
        }
    }

    // Hands a link from a second Keystrap start to the window that's already open, over a per-user named pipe.
    static class LinkPipe
    {
        public static readonly string Name = "Keystrap.Link." + Environment.UserName;
        public const string Activate = "activate";

        public static bool Send(string message)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out))
                {
                    pipe.Connect(3000);
                    byte[] data = Encoding.UTF8.GetBytes(message);
                    pipe.Write(data, 0, data.Length);
                    pipe.Flush();
                    pipe.WaitForPipeDrain();
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Link handoff failed: " + ex.Message);
                return false;
            }
        }

        // Runs for the life of the app on a background thread; onMessage is called on that thread.
        public static void Listen(Action<string> onMessage)
        {
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(Name, PipeDirection.In, 1))
                        {
                            server.WaitForConnection();
                            using (var reader = new StreamReader(server, Encoding.UTF8))
                            {
                                string message = reader.ReadToEnd();
                                if (message.Length > 0) onMessage(message);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Link listener error: " + ex.Message);
                        Thread.Sleep(1000);
                    }
                }
            });
            thread.IsBackground = true;
            thread.Name = "Keystrap link listener";
            thread.Start();
        }
    }

    static class Display
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
        const int ENUM_CURRENT_SETTINGS = -1;

        // Current refresh rate of the primary display in Hz, or 0 when Windows reports only "hardware default".
        public static int PrimaryRefreshRate()
        {
            try
            {
                var mode = new DEVMODE();
                mode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (!EnumDisplaySettings(Screen.PrimaryScreen.DeviceName, ENUM_CURRENT_SETTINGS, ref mode)) return 0;
                return mode.dmDisplayFrequency > 1 ? mode.dmDisplayFrequency : 0;
            }
            catch (Exception ex)
            {
                Log.Write("Refresh rate lookup failed: " + ex.Message);
                return 0;
            }
        }
    }

    class RobloxVersion
    {
        public string Version;   // e.g. 0.744.0.7441080 (may be null when only the GUID is known)
        public string Guid;      // e.g. version-9ac8595712b447a7
    }

    // Installs Roblox the way Bloxstrap does: read the version's package manifest from Roblox's CDN,
    // download and checksum each package, extract it to its folder, then write AppSettings.xml.
    static class Bootstrapper
    {
        public const string PlayerExe = "RobloxPlayerBeta.exe";
        const string Cdn = "https://setup.rbxcdn.com/";
        const string Marker = "keystrap.installed";
        const string AppSettingsXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<Settings>\r\n\t<ContentFolder>content</ContentFolder>\r\n\t<BaseUrl>http://www.roblox.com</BaseUrl>\r\n</Settings>\r\n";

        // Where each package from rbxPkgManifest.txt is extracted, relative to the version folder.
        static readonly Dictionary<string, string> PackageDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "RobloxApp.zip", "" },
            { "redist.zip", "" },
            { "WebView2.zip", "" },
            { "shaders.zip", @"shaders\" },
            { "ssl.zip", @"ssl\" },
            { "WebView2RuntimeInstaller.zip", @"WebView2RuntimeInstaller\" },
            { "content-avatar.zip", @"content\avatar\" },
            { "content-configs.zip", @"content\configs\" },
            { "content-fonts.zip", @"content\fonts\" },
            { "content-sky.zip", @"content\sky\" },
            { "content-sounds.zip", @"content\sounds\" },
            { "content-textures2.zip", @"content\textures\" },
            { "content-models.zip", @"content\models\" },
            { "content-platform-fonts.zip", @"PlatformContent\pc\fonts\" },
            { "content-platform-dictionaries.zip", @"PlatformContent\pc\shared_compression_dictionaries\" },
            { "content-terrain.zip", @"PlatformContent\pc\terrain\" },
            { "content-textures3.zip", @"PlatformContent\pc\textures\" },
            { "extracontent-luapackages.zip", @"ExtraContent\LuaPackages\" },
            { "extracontent-translations.zip", @"ExtraContent\translations\" },
            { "extracontent-models.zip", @"ExtraContent\models\" },
            { "extracontent-textures.zip", @"ExtraContent\textures\" },
            { "extracontent-places.zip", @"ExtraContent\places\" },
        };

        class Package
        {
            public string Name;
            public string Md5;
            public long PackedSize;
        }

        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
            return client;
        }

        public static async Task<RobloxVersion> GetLatestAsync()
        {
            try
            {
                string json = await http.GetStringAsync("https://clientsettings.roblox.com/v2/client-version/WindowsPlayer");
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                var v = new RobloxVersion();
                v.Version = (string)d["version"];
                v.Guid = (string)d["clientVersionUpload"];
                return v;
            }
            catch (Exception ex)
            {
                Log.Write("clientsettings.roblox.com lookup failed (" + ex.Message + "), trying setup.rbxcdn.com/versionGuid");
            }
            // Fallback: the older plain-text endpoint (C# 5 can't await inside catch).
            string guid = (await http.GetStringAsync(Cdn + "versionGuid")).Trim();
            if (!guid.StartsWith("version-")) throw new InvalidDataException("Roblox returned an unexpected version: " + guid);
            var fallback = new RobloxVersion();
            fallback.Guid = guid;
            return fallback;
        }

        public static RobloxVersion GetInstalled()
        {
            if (!Directory.Exists(Paths.Versions)) return null;
            RobloxVersion best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string dir in Directory.GetDirectories(Paths.Versions))
            {
                string marker = Path.Combine(dir, Marker);
                if (!File.Exists(marker) || !File.Exists(Path.Combine(dir, PlayerExe))) continue;
                DateTime t = File.GetLastWriteTimeUtc(marker);
                if (t <= bestTime) continue;
                bestTime = t;
                best = new RobloxVersion();
                best.Guid = Path.GetFileName(dir);
                string version = File.ReadAllText(marker).Split('|')[0];
                best.Version = version.Length > 0 ? version : null;
            }
            return best;
        }

        public static long VersionsSize()
        {
            long total = 0;
            if (!Directory.Exists(Paths.Versions)) return 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(Paths.Versions, "*", SearchOption.AllDirectories))
                    total += new FileInfo(f).Length;
            }
            catch (Exception ex) { Log.Write("Size scan failed: " + ex.Message); }
            return total;
        }

        // Returns the folder holding a ready-to-run copy of this version, downloading it if needed.
        public static async Task<string> InstallAsync(RobloxVersion v, Action<string, double> progress)
        {
            string dir = Path.Combine(Paths.Versions, v.Guid);
            if (File.Exists(Path.Combine(dir, Marker)) && File.Exists(Path.Combine(dir, PlayerExe)))
            {
                Log.Write("Roblox " + v.Guid + " is already installed");
                return dir;
            }

            Log.Write("Installing Roblox " + (v.Version ?? "") + " " + v.Guid);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Paths.Downloads);

            progress("Reading Roblox's package list…", 0.03);
            List<Package> packages = ParseManifest(await http.GetStringAsync(Cdn + v.Guid + "-rbxPkgManifest.txt"));
            long total = Math.Max(1, packages.Sum(p => p.PackedSize));
            long done = 0;

            foreach (Package pkg in packages)
            {
                string target;
                if (!PackageDirs.TryGetValue(pkg.Name, out target))
                {
                    if (pkg.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) Log.Write("Skipping unknown package " + pkg.Name);
                    done += pkg.PackedSize;
                    continue;
                }

                string file = Path.Combine(Paths.Downloads, v.Guid + "-" + pkg.Name);
                long start = done;
                string label = "Downloading Roblox (" + pkg.Name + ")…";
                await DownloadVerifiedAsync(Cdn + v.Guid + "-" + pkg.Name, file, pkg,
                    bytes => progress(label, 0.05 + 0.85 * (start + bytes) / total));

                progress("Extracting " + pkg.Name + "…", 0.05 + 0.85 * (start + pkg.PackedSize) / total);
                Extract(file, Path.Combine(dir, target));
                File.Delete(file);
                done += pkg.PackedSize;
            }

            File.WriteAllText(Path.Combine(dir, "AppSettings.xml"), AppSettingsXml);
            if (!File.Exists(Path.Combine(dir, PlayerExe)))
                throw new InvalidDataException("The download finished, but " + PlayerExe + " is missing from it.");

            File.WriteAllText(Path.Combine(dir, Marker), (v.Version ?? "") + "|" + DateTime.UtcNow.ToString("o"));
            Log.Write("Installed Roblox to " + dir);
            RemoveOldVersions(v.Guid);
            return dir;
        }

        static List<Package> ParseManifest(string text)
        {
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
            if (lines.Length == 0 || lines[0] != "v0") throw new InvalidDataException("Roblox's package list is in an unknown format.");
            var list = new List<Package>();
            // After the "v0" header, each package is four lines: name, MD5, packed size, unpacked size.
            for (int i = 1; i + 3 < lines.Length; i += 4)
            {
                var p = new Package();
                p.Name = lines[i];
                p.Md5 = lines[i + 1].ToLowerInvariant();
                p.PackedSize = long.Parse(lines[i + 2]);
                list.Add(p);
            }
            return list;
        }

        static async Task DownloadVerifiedAsync(string url, string file, Package pkg, Action<long> onBytes)
        {
            if (File.Exists(file) && Md5Of(file) == pkg.Md5) { onBytes(pkg.PackedSize); return; }
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using (HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        resp.EnsureSuccessStatusCode();
                        using (Stream src = await resp.Content.ReadAsStreamAsync())
                        using (FileStream dst = File.Create(file))
                        {
                            var buffer = new byte[81920];
                            long read = 0;
                            int n;
                            while ((n = await src.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await dst.WriteAsync(buffer, 0, n);
                                read += n;
                                onBytes(read);
                            }
                        }
                    }
                    if (Md5Of(file) == pkg.Md5) return;
                    throw new InvalidDataException(pkg.Name + " failed its checksum check.");
                }
                catch (Exception ex)
                {
                    Log.Write("Download of " + pkg.Name + " failed (attempt " + attempt + "): " + ex.Message);
                    if (attempt >= 3) throw;
                }
            }
        }

        static string Md5Of(string file)
        {
            using (var md5 = MD5.Create())
            using (FileStream s = File.OpenRead(file))
                return BitConverter.ToString(md5.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        static void Extract(string zip, string dest)
        {
            string root = Path.GetFullPath(dest);
            if (!root.EndsWith("\\")) root += "\\";
            using (ZipArchive archive = ZipFile.OpenRead(zip))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // folder entry
                    string path = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', '\\').TrimStart('\\')));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // never write outside the version folder
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    entry.ExtractToFile(path, true);
                }
            }
        }

        static void RemoveOldVersions(string keepGuid)
        {
            foreach (string dir in Directory.GetDirectories(Paths.Versions))
            {
                if (string.Equals(Path.GetFileName(dir), keepGuid, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, true); Log.Write("Removed old version " + dir); }
                catch (Exception ex) { Log.Write("Couldn't remove old version " + dir + ": " + ex.Message); }
            }
            try { if (Directory.Exists(Paths.Downloads)) Directory.Delete(Paths.Downloads, true); } catch { }
        }

        public static void WriteClientSettings(string dir, Dictionary<string, string> flags)
        {
            string folder = Path.Combine(dir, "ClientSettings");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "ClientAppSettings.json"), new JavaScriptSerializer().Serialize(flags));
            Log.Write("Wrote " + flags.Count + " flags to ClientAppSettings.json");
        }

        // Roblox's in-game Maximum Frame Rate, kept in its own settings file. Roblox only accepts allow-listed flags from
        // ClientAppSettings.json and DFIntTaskSchedulerTargetFps isn't one ("Denied local configuration" in its log), so
        // this is the setting that actually sets the frame rate. -1 ("not set") leaves Roblox on its 60 FPS default.
        // Never throws; Roblox may be rewriting the file.
        public static void SetFramerateCap(int fps)
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox");
                if (!Directory.Exists(folder)) return;
                foreach (string file in Directory.GetFiles(folder, "GlobalBasicSettings_*.xml"))
                {
                    string xml = File.ReadAllText(file);
                    string updated = Regex.Replace(xml, @"(<int name=""FramerateCap"">)-?\d+(</int>)",
                        "${1}" + fps.ToString(CultureInfo.InvariantCulture) + "${2}");
                    if (updated == xml) continue;
                    File.WriteAllText(file, updated);
                    Log.Write("Set Roblox's Maximum Frame Rate to " + fps + " in " + Path.GetFileName(file));
                }
            }
            catch (Exception ex) { Log.Write("Couldn't set Roblox's Maximum Frame Rate: " + ex.Message); }
        }

        // link: the website's roblox-player:/roblox: link, passed through unchanged; null opens the Roblox app home.
        public static Process Start(string dir, string link)
        {
            var psi = new ProcessStartInfo(Path.Combine(dir, PlayerExe), link != null ? "\"" + link.Replace("\"", "") + "\"" : "--app");
            psi.WorkingDirectory = dir;
            psi.UseShellExecute = false;
            Log.Write("Starting " + psi.FileName + (link != null ? " with a Roblox link (" + RobloxLink.Describe(link) + ")" : " --app"));
            return Process.Start(psi);
        }
    }

    // Wraps the Windows spell checker (Windows 8+, ISpellCheckerFactory), so corrections need no download or API key.
    static class SpellCheck
    {
        [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISpellCheckerFactory
        {
            ComTypes.IEnumString SupportedLanguages();
            int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
            ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        }

        // Only the first vtable slots are declared; the rest of ISpellChecker isn't used.
        [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISpellChecker
        {
            [return: MarshalAs(UnmanagedType.LPWStr)] string LanguageTag();
            IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
            ComTypes.IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
        }

        [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IEnumSpellingError
        {
            [PreserveSig] int Next(out ISpellingError value);
        }

        [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISpellingError
        {
            uint StartIndex();
            uint Length();
            int CorrectiveAction();   // 1 = get suggestions, 2 = replace, 3 = delete (repeated word)
            [return: MarshalAs(UnmanagedType.LPWStr)] string Replacement();
        }

        [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
        class SpellCheckerFactoryClass { }

        // Words the dictionary doesn't know but shouldn't "correct".
        static readonly HashSet<string> KnownTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Keystrap", "Bloxstrap", "Fishstrap", "Roblox", "Robux", "FFlag", "FFlags", "FastFlag", "FastFlags",
            "Discord", "FPS", "RAM", "GPU", "CPU", "UI", "Vulkan", "OpenGL", "DirectX", "D3D11", "skybox", "multi-instance",
        };

        static ISpellChecker Create()
        {
            try
            {
                var factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
                foreach (string tag in new[] { "en-US", "en-GB" })
                    if (factory.IsSupported(tag) != 0) return factory.CreateSpellChecker(tag);
                Log.Write("Windows spell checker has no English dictionary");
            }
            catch (Exception ex) { Log.Write("Windows spell checker unavailable: " + ex.Message); }
            return null;
        }

        public static string Correct(string text)
        {
            ISpellChecker checker = Create();
            if (checker == null) return text;
            var fixes = new List<Tuple<int, int, string>>();
            try
            {
                IEnumSpellingError errors = checker.Check(text);
                ISpellingError e;
                while (errors.Next(out e) == 0 && e != null)
                {
                    int start = (int)e.StartIndex(), len = (int)e.Length();
                    if (start < 0 || len <= 0 || start + len > text.Length) continue;
                    string word = text.Substring(start, len);
                    if (KnownTerms.Contains(word)) continue;
                    string replacement = null;
                    switch (e.CorrectiveAction())
                    {
                        case 1: replacement = BestSuggestion(checker, word); break;
                        case 2: replacement = e.Replacement(); break;
                        case 3: replacement = ""; break;
                    }
                    if (replacement != null) fixes.Add(Tuple.Create(start, len, replacement));
                }
            }
            catch (Exception ex)
            {
                Log.Write("Spell check failed: " + ex.Message);
                return text;
            }
            // Apply from the end so earlier offsets stay valid.
            foreach (var f in fixes.OrderByDescending(f => f.Item1))
                text = text.Remove(f.Item1, f.Item2).Insert(f.Item1, f.Item3);
            return Regex.Replace(text, " {2,}", " ");
        }

        // Windows ranks suggestions loosely ("rember" -> "ember"), so pick from its top few: same first letter,
        // no apostrophe the user didn't type, then the fewest edits, then Windows' order.
        static string BestSuggestion(ISpellChecker checker, string word)
        {
            var list = new List<string>();
            ComTypes.IEnumString e = checker.Suggest(word);
            var one = new string[1];
            while (list.Count < 6 && e.Next(1, one, IntPtr.Zero) == 0) list.Add(one[0]);
            if (list.Count == 0) return null;
            char first = char.ToLowerInvariant(word[0]);
            bool apostrophe = word.Contains("'");
            return list
                .Select((s, i) => new { s, i })
                .OrderBy(x => char.ToLowerInvariant(x.s[0]) == first ? 0 : 1)
                .ThenBy(x => !apostrophe && x.s.Contains("'") ? 1 : 0)
                .ThenBy(x => EditDistance(word.ToLowerInvariant(), x.s.ToLowerInvariant()))
                .ThenBy(x => x.i)
                .First().s;
        }

        // Optimal string alignment distance: insertions, deletions, substitutions and adjacent swaps.
        static int EditDistance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            return d[a.Length, b.Length];
        }
    }

    class Suggestion
    {
        public string Category;   // Bug report, Feature request or General feedback
        public string Summary;    // first sentence, shortened
        public string Details;    // the corrected text
        public string Benefit;
        public string NextStep;
    }

    // The Support page's suggestion box: tidy up what the user wrote, then post it to the developers' Discord.
    static class Feedback
    {
        const string WebhookUrl = Secrets.FeedbackWebhook;   // from secrets.json, see build.ps1
        const string AppVersion = AppInfo.Version;
        public const int MinLength = 10;
        public const int MaxLength = 1000;
        static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
        static DateTime lastSentUtc = DateTime.MinValue;
        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
            return client;
        }

        // Chat shorthand and missing apostrophes, matched as whole words.
        static readonly Dictionary<string, string> Shorthand = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "i", "I" }, { "im", "I'm" }, { "ive", "I've" }, { "dont", "don't" }, { "cant", "can't" }, { "wont", "won't" },
            { "isnt", "isn't" }, { "doesnt", "doesn't" }, { "didnt", "didn't" }, { "wasnt", "wasn't" }, { "arent", "aren't" },
            { "couldnt", "couldn't" }, { "shouldnt", "shouldn't" }, { "wouldnt", "wouldn't" }, { "havent", "haven't" },
            { "thats", "that's" }, { "whats", "what's" }, { "theres", "there's" }, { "u", "you" }, { "ur", "your" },
            { "pls", "please" }, { "plz", "please" }, { "thx", "thanks" }, { "alot", "a lot" }, { "idk", "I don't know" },
            { "imo", "in my opinion" }, { "btw", "by the way" }, { "cuz", "because" }, { "bc", "because" },
            { "gonna", "going to" }, { "wanna", "want to" }, { "ppl", "people" }, { "rn", "right now" },
            { "kinda", "kind of" }, { "sorta", "sort of" }, { "ui", "UI" }, { "fps", "FPS" },
            // Typos the spell checker tends to "fix" into the wrong word.
            { "relly", "really" }, { "realy", "really" }, { "rember", "remember" }, { "remeber", "remember" },
            { "lanch", "launch" }, { "luanch", "launch" }, { "lauch", "launch" }, { "crashs", "crashes" },
            { "anoying", "annoying" }, { "definately", "definitely" }, { "recieve", "receive" }, { "seperate", "separate" },
            { "untill", "until" }, { "wierd", "weird" }, { "becuase", "because" }, { "becasue", "because" },
            { "settigns", "settings" }, { "shoud", "should" }, { "woud", "would" }, { "coud", "could" },
        };

        public static Suggestion Polish(string raw)
        {
            string text = Tidy(raw);
            text = Regex.Replace(text, @"\b[A-Za-z]+\b", m =>
            {
                string r;
                if (!Shorthand.TryGetValue(m.Value, out r)) return m.Value;
                return char.IsUpper(m.Value[0]) ? char.ToUpperInvariant(r[0]) + r.Substring(1) : r;
            });
            text = SpellCheck.Correct(text);
            text = Regex.Replace(text, @"\b([Aa]) (?=[aeioAEIO])", "$1n ");   // "a option" -> "an option"
            text = Regex.Replace(text, @"\b([Ii])ts (?=(a|an|the|not|so|very|really|too|been|going|just|kind of|hard|annoying|impossible|broken|fine|ok|okay)\b)", "$1t's ");
            text = Punctuate(text);

            var s = new Suggestion();
            s.Details = text;
            s.Category = Categorize(text);
            s.Summary = Summarize(text);
            switch (s.Category)
            {
                case "Bug report":
                    s.Benefit = "Fixing this would make Keystrap more reliable and keep the problem from interrupting play for anyone else who runs into it.";
                    s.NextStep = "Try to reproduce the issue on " + AppVersion + " and check the user's Keystrap logs (%LocalAppData%\\Keystrap\\Logs) for related errors.";
                    break;
                case "Feature request":
                    s.Benefit = "This would give players more control over how Keystrap works and save them time on something they can't do today, or have to do by hand.";
                    s.NextStep = "Gauge how many users would benefit, then consider adding it as an option in an upcoming release.";
                    break;
                default:
                    s.Benefit = "Acting on this feedback would improve the overall Keystrap experience.";
                    s.NextStep = "Review the feedback and decide whether it calls for a change in an upcoming release.";
                    break;
            }
            return s;
        }

        static string Tidy(string raw)
        {
            var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => Regex.Replace(l, @"\s+", " ").Trim());
            return Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim();
        }

        static string Punctuate(string text)
        {
            text = Regex.Replace(text, @"([!?])\1+", "$1");                      // "!!!" -> "!"
            text = Regex.Replace(text, @"[ \t]+([,.!?;:])", "$1");               // "word ," -> "word,"
            text = Regex.Replace(text, @"([,;!?])(?=[A-Za-z])", "$1 ");          // "a,b" -> "a, b"
            text = Regex.Replace(text, @"(?<=[a-z]{2})\.(?=[A-Z][a-z])", ". ");  // "game.Please" -> "game. Please"
            // Capital letter at the start of every sentence and line.
            text = Regex.Replace(text, @"(^|[.!?]\s+|\n)([a-z])", m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
            // Every line ends with punctuation.
            return string.Join("\n", text.Split('\n').Select(l => Regex.IsMatch(l, @"[A-Za-z0-9)""']$") ? l + "." : l));
        }

        static string Categorize(string text)
        {
            string l = text.ToLowerInvariant();
            if (Regex.IsMatch(l, @"\b(crash\w*|bugs?|bugged|broken|errors?|freez\w*|not working|doesn't work|does not work|fails?|failed|glitch\w*|stuck|lags?|laggy)\b"))
                return "Bug report";
            if (Regex.IsMatch(l, @"\b(add|adding|feature|option|setting|support for|would be (nice|cool|great|awesome)|could you|can you|please|wish|allow|let us|ability|make it)\b"))
                return "Feature request";
            return "General feedback";
        }

        static string Summarize(string text)
        {
            Match m = Regex.Match(text, @"^.+?[.!?](?=\s|$)");
            string first = m.Success ? m.Value : text.Split('\n')[0];
            if (first.Length <= 90) return first;
            int cut = first.LastIndexOf(' ', 87);
            return first.Substring(0, cut > 40 ? cut : 87).TrimEnd(',', ';', ':') + "…";
        }

        static string Clip(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        public static async Task SendAsync(string raw, Suggestion s, string user)
        {
            DateTime now = DateTime.UtcNow;
            if (now - lastSentUtc < Cooldown)
                throw new InvalidOperationException("You just sent a suggestion. Wait a minute before sending another.");

            long unix = (long)(now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            string polished = "**" + s.Summary + "**\n\n" + s.Details +
                "\n\n**Why it helps**\n" + s.Benefit + "\n\n**Suggested next step**\n" + s.NextStep;
            var embed = new Dictionary<string, object>
            {
                { "title", Clip("New suggestion · " + s.Category, 256) },
                { "color", 0xFFFBA6 },
                { "description", Clip(polished, 4000) },
                { "fields", new object[]
                    {
                        Field("App Version", AppVersion, true),
                        Field("User", Clip(user, 100), true),
                        Field("Submitted", "<t:" + unix + ":F>", true),
                        Field("Original suggestion", "```\n" + Clip(raw.Replace("```", "'''"), 1000) + "\n```", false),
                    }
                },
                { "footer", new Dictionary<string, object> { { "text", "Keystrap suggestion box · polished automatically" } } },
                { "timestamp", now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
            };
            var payload = new Dictionary<string, object>
            {
                { "username", "Keystrap Suggestions" },
                { "allowed_mentions", new Dictionary<string, object> { { "parse", new string[0] } } }, // no @everyone from user text
                { "embeds", new object[] { embed } },
            };

            if (WebhookUrl.Length == 0) throw new InvalidOperationException("The suggestion box isn't set up in this build of Keystrap.");
            var body = new StringContent(new JavaScriptSerializer().Serialize(payload), Encoding.UTF8, "application/json");
            using (HttpResponseMessage resp = await http.PostAsync(WebhookUrl, body))
            {
                if ((int)resp.StatusCode == 429)
                    throw new InvalidOperationException("The suggestion box is busy right now. Wait a minute and try again.");
                if (!resp.IsSuccessStatusCode)
                {
                    Log.Write("Discord webhook returned " + (int)resp.StatusCode + ": " + await resp.Content.ReadAsStringAsync());
                    throw new InvalidOperationException("The suggestion box didn't accept the message (error " + (int)resp.StatusCode + "). Try again later.");
                }
            }
            lastSentUtc = now;
        }

        static Dictionary<string, object> Field(string name, string value, bool inline)
        {
            return new Dictionary<string, object> { { "name", name }, { "value", value }, { "inline", inline } };
        }
    }

    // Playtime: follows Roblox's own log files (%LocalAppData%\Roblox\logs) the way Bloxstrap's activity watcher
    // does. A "Joining game" line opens a session; a disconnect line, the next join (teleport) or Roblox closing
    // ends it. Sessions are stored in playtime.json and combined per experience (universe ID) for the page.
    class PlaytimeTracker
    {
        class Session
        {
            public long Universe;   // experience ID; 0 until known
            public long Place;
            public string JobId;    // server ID, used with Start to avoid recording a session twice
            public DateTime Start;  // UTC, from the log line timestamps
            public DateTime End;
        }

        class LogState
        {
            public long Pos;            // bytes of the log already read
            public Session Open;        // game this Roblox window is in right now
            public DateTime LastStamp;  // newest timestamp seen in this log
        }

        static readonly string RobloxLogs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
        static readonly string StoreFile = Path.Combine(Paths.Data, "playtime.json");
        static readonly TimeSpan MinSession = TimeSpan.FromSeconds(10);
        static readonly TimeSpan CatchUp = TimeSpan.FromMinutes(30);   // logs this recent are read in full at startup
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static readonly Regex Stamp = new Regex(@"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z)", RegexOptions.Compiled);
        static readonly Regex JoinLine = new Regex(@"\[FLog::Output\] ! Joining game '([0-9a-fA-F\-]+)' place (\d+)", RegexOptions.Compiled);
        static readonly Regex UniverseLine = new Regex(@"\[FLog::GameJoinLoadTime\] Report game_join_loadtime: placeid:(\d+).*?universeid:(\d+)", RegexOptions.Compiled);
        static readonly Regex LeaveLine = new Regex(@"\[FLog::Network\] Time to disconnect replication data|\[FLog::SingleSurfaceApp\] leaveUGCGameInternal|\[FLog::Network\] Connection lost", RegexOptions.Compiled);

        // Title cleanup: bracketed tags like "[UPDATE 5]" or "(NEW!)", emoji and symbols, then stray punctuation at the ends.
        static readonly Regex TitleBrackets = new Regex(@"[\[(【〔「『{<（［][^\]\)】〕」』}>）］]{0,40}[\])】〕」』}>）］]", RegexOptions.Compiled);
        static readonly Regex TitleSymbols = new Regex(@"\p{Cs}|\p{So}|\p{Sk}|\p{Co}|\p{Cf}|[\u20E3\uFE0E\uFE0F]", RegexOptions.Compiled);
        static readonly Regex TitleEdges = new Regex(@"^[\s\p{Pd}|•·~:!*_.,/\\+]+|[\s\p{Pd}|•·~:*_.,/\\+]+$", RegexOptions.Compiled);
        static readonly Regex TitleSpaces = new Regex(@"\s{2,}", RegexOptions.Compiled);

        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
            return client;
        }

        readonly List<Session> sessions = new List<Session>();
        readonly HashSet<string> recorded = new HashSet<string>();
        readonly Dictionary<string, string> titles = new Dictionary<string, string>();   // "u<universe>" -> clean title
        readonly Dictionary<long, long> placeUniverse = new Dictionary<long, long>();
        readonly Dictionary<string, LogState> logs = new Dictionary<string, LogState>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<long> resolving = new HashSet<long>();
        readonly DateTime startedUtc = DateTime.UtcNow;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly Action changed;   // runs on the UI thread whenever the numbers the page shows change
        bool wasPlaying;
        DateTime lastTitleRetry = DateTime.UtcNow;

        public PlaytimeTracker(Action changed)
        {
            this.changed = changed;
        }

        // Reads playtime.json; safe on a background thread because polling hasn't started yet.
        public void LoadFromDisk() { Load(); }

        // Looks up names that weren't known when sessions were saved. Call on the UI thread.
        public void ResolveMissingTitles()
        {
            foreach (long place in sessions.Where(s => TitleOf(s) == null).Select(s => s.Place).Distinct().ToList())
                ResolveTitle(place);
        }

        public static string CleanTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string t = TitleBrackets.Replace(raw, " ");
            t = TitleSymbols.Replace(t, " ");
            t = TitleSpaces.Replace(TitleEdges.Replace(t, ""), " ").Trim();
            return t.Length > 0 ? t : raw.Trim();
        }

        // Called every few seconds from a UI timer.
        public void Poll()
        {
            bool any = false;
            Process[] procs = Process.GetProcessesByName("RobloxPlayerBeta");
            bool running = procs.Length > 0;
            foreach (Process p in procs) p.Dispose();

            if (Directory.Exists(RobloxLogs))
            {
                foreach (FileInfo fi in new DirectoryInfo(RobloxLogs).GetFiles("*_Player_*.log"))
                {
                    if (fi.Name.IndexOf("CrashHandler", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    LogState st;
                    if (!logs.TryGetValue(fi.FullName, out st))
                    {
                        if (fi.LastWriteTimeUtc < startedUtc - CatchUp) continue;
                        st = new LogState();
                        logs[fi.FullName] = st;
                    }
                    if (fi.Length < st.Pos) st.Pos = 0;   // log was replaced
                    if (fi.Length > st.Pos) any |= ReadNew(fi.FullName, st);
                }
            }

            // Roblox closed (or crashed) without a leave line: end its session at the last line it logged.
            if (!running)
                foreach (LogState st in logs.Values)
                    if (st.Open != null) any |= Close(st, st.LastStamp);

            // Retry names that couldn't be looked up (offline, Roblox API hiccup) once a minute.
            if (DateTime.UtcNow - lastTitleRetry > TimeSpan.FromMinutes(1))
            {
                lastTitleRetry = DateTime.UtcNow;
                foreach (long place in sessions.Where(s => TitleOf(s) == null).Select(s => s.Place).Distinct().ToList())
                    ResolveTitle(place);
            }

            bool playing = logs.Values.Any(s => s.Open != null);
            if (any || playing != wasPlaying)
            {
                wasPlaying = playing;
                changed();
            }
        }

        bool ReadNew(string path, LogState st)
        {
            string text;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Seek(st.Pos, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - st.Pos];
                    int n = 0, r;
                    while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
                    if (n == 0) return false;
                    int end = Array.LastIndexOf(buf, (byte)'\n', n - 1);   // only whole lines; the rest is read next time
                    if (end < 0) return false;
                    text = Encoding.UTF8.GetString(buf, 0, end + 1);
                    st.Pos += end + 1;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }

            bool any = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimStart('﻿');   // a byte-order mark can precede the first line
                Match ts = Stamp.Match(line);
                if (!ts.Success) continue;
                DateTime when = DateTime.Parse(ts.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                st.LastStamp = when;

                Match m = JoinLine.Match(line);
                if (m.Success)
                {
                    if (st.Open != null) Close(st, when);   // teleported to another place
                    var s = new Session();
                    s.JobId = m.Groups[1].Value;
                    s.Place = long.Parse(m.Groups[2].Value);
                    s.Start = when;
                    placeUniverse.TryGetValue(s.Place, out s.Universe);
                    st.Open = s;
                    ResolveTitle(s.Place);
                    any = true;
                    continue;
                }
                m = UniverseLine.Match(line);
                if (m.Success)
                {
                    long place = long.Parse(m.Groups[1].Value), universe = long.Parse(m.Groups[2].Value);
                    placeUniverse[place] = universe;
                    if (st.Open != null && st.Open.Place == place && st.Open.Universe != universe)
                    {
                        st.Open.Universe = universe;
                        ResolveTitle(place);
                        any = true;
                    }
                    continue;
                }
                if (st.Open != null && LeaveLine.IsMatch(line)) any |= Close(st, when);
            }
            return any;
        }

        bool Close(LogState st, DateTime end)
        {
            Session s = st.Open;
            st.Open = null;
            if (s == null) return false;
            s.End = end;
            if (s.End - s.Start < MinSession) return true;
            if (!recorded.Add(s.JobId + "|" + s.Start.Ticks)) return true;   // already saved by an earlier run
            if (s.Universe == 0) placeUniverse.TryGetValue(s.Place, out s.Universe);
            sessions.Add(s);
            Log.Write("Playtime: " + (TitleOf(s) ?? "place " + s.Place) + " for " + (int)(s.End - s.Start).TotalMinutes + " min");
            Save();
            return true;
        }

        long UniverseOf(Session s)
        {
            long u;
            return s.Universe > 0 ? s.Universe : placeUniverse.TryGetValue(s.Place, out u) ? u : 0;
        }

        string GameKey(Session s)
        {
            long u = UniverseOf(s);
            return u > 0 ? "u" + u : "p" + s.Place;
        }

        string TitleOf(Session s)
        {
            long u = UniverseOf(s);
            string t;
            return u > 0 && titles.TryGetValue("u" + u, out t) ? t : null;
        }

        // Looks up the experience name through Roblox's public APIs (place -> universe -> name), once per universe.
        async void ResolveTitle(long place)
        {
            long universe;
            placeUniverse.TryGetValue(place, out universe);
            if ((universe > 0 && titles.ContainsKey("u" + universe)) || !resolving.Add(place)) return;
            try
            {
                if (universe == 0)
                {
                    var u = json.Deserialize<Dictionary<string, object>>(
                        await http.GetStringAsync("https://apis.roblox.com/universes/v1/places/" + place + "/universe"));
                    object id;
                    if (!u.TryGetValue("universeId", out id) || id == null) return;
                    universe = Convert.ToInt64(id, CultureInfo.InvariantCulture);
                    placeUniverse[place] = universe;
                }
                if (!titles.ContainsKey("u" + universe))
                {
                    var g = json.Deserialize<Dictionary<string, object>>(
                        await http.GetStringAsync("https://games.roblox.com/v1/games?universeIds=" + universe));
                    var data = g.ContainsKey("data") ? g["data"] as ArrayList : null;
                    var game = data != null && data.Count > 0 ? data[0] as Dictionary<string, object> : null;
                    string name = game != null && game.ContainsKey("name") ? game["name"] as string : null;
                    if (name == null) return;
                    titles["u" + universe] = CleanTitle(name);
                }
                Save();
                changed();
            }
            catch (Exception ex)
            {
                Log.Write("Playtime: couldn't look up the name of place " + place + ": " + ex.Message);
            }
            finally
            {
                resolving.Remove(place);
            }
        }

        static long Ms(DateTime utc) { return (long)(utc - Epoch).TotalMilliseconds; }

        public class LiveGame
        {
            public string Title;     // cleaned title, or null until it's been looked up
            public long Place;
            public long Universe;    // 0 until known
            public DateTime Start;   // UTC
        }

        // The game being played right now (the most recently joined, if several Roblox windows are open), or null.
        public LiveGame CurrentGame()
        {
            LogState st = logs.Values.Where(s => s.Open != null).OrderByDescending(s => s.Open.Start).FirstOrDefault();
            if (st == null) return null;
            var g = new LiveGame();
            g.Title = TitleOf(st.Open);
            g.Place = st.Open.Place;
            g.Universe = UniverseOf(st.Open);
            g.Start = st.Open.Start;
            return g;
        }

        public const int TopGames = 10;

        // What the Playtime page shows: the 10 most played games (one entry per game, most played first), totals across
        // every game, and any game being played now.
        public Dictionary<string, object> Snapshot()
        {
            var all = sessions
                .GroupBy(GameKey)
                .Select(g =>
                {
                    Session last = g.OrderBy(s => s.End).Last();
                    return new Dictionary<string, object>
                    {
                        { "id", g.Key },
                        { "title", TitleOf(last) ?? "Place " + last.Place },
                        { "placeId", last.Place },
                        { "seconds", (long)g.Sum(s => (s.End - s.Start).TotalSeconds) },
                        { "sessions", g.Count() },
                        { "lastPlayed", Ms(last.End) },
                    };
                })
                .OrderByDescending(d => (long)d["seconds"])
                .ToList();
            var games = all.Take(TopGames).ToList();
            var current = logs.Values.Where(st => st.Open != null).Select(st => new Dictionary<string, object>
            {
                { "title", TitleOf(st.Open) ?? "Place " + st.Open.Place },
                { "placeId", st.Open.Place },
                { "start", Ms(st.Open.Start) },
            }).ToList();
            return new Dictionary<string, object>
            {
                { "type", "playtime" },
                { "games", games },
                { "current", current },
                { "totalSeconds", all.Sum(d => (long)d["seconds"]) },
                { "gameCount", all.Count },
                { "sessionCount", all.Sum(d => (int)d["sessions"]) },
            };
        }

        void Load()
        {
            try
            {
                if (!File.Exists(StoreFile)) return;
                var d = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(StoreFile));
                object o;
                if (d.TryGetValue("titles", out o) && o is Dictionary<string, object>)
                    foreach (var kv in (Dictionary<string, object>)o) titles[kv.Key] = kv.Value as string;
                if (d.TryGetValue("places", out o) && o is Dictionary<string, object>)
                    foreach (var kv in (Dictionary<string, object>)o)
                        placeUniverse[long.Parse(kv.Key, CultureInfo.InvariantCulture)] = Convert.ToInt64(kv.Value, CultureInfo.InvariantCulture);
                if (d.TryGetValue("sessions", out o) && o is ArrayList)
                    foreach (Dictionary<string, object> r in (ArrayList)o)
                    {
                        var s = new Session();
                        s.Universe = Convert.ToInt64(r["u"], CultureInfo.InvariantCulture);
                        s.Place = Convert.ToInt64(r["p"], CultureInfo.InvariantCulture);
                        s.JobId = r["job"] as string;
                        s.Start = Epoch.AddMilliseconds(Convert.ToDouble(r["start"], CultureInfo.InvariantCulture));
                        s.End = Epoch.AddMilliseconds(Convert.ToDouble(r["end"], CultureInfo.InvariantCulture));
                        sessions.Add(s);
                        recorded.Add(s.JobId + "|" + s.Start.Ticks);
                    }
            }
            catch (Exception ex)
            {
                Log.Write("playtime.json couldn't be read, starting fresh: " + ex.Message);
                try { File.Copy(StoreFile, StoreFile + ".bad", true); } catch { }
            }
        }

        void Save()
        {
            try
            {
                var data = new Dictionary<string, object>
                {
                    { "version", 1 },                    // file format
                    { "appVersion", AppInfo.Version },   // Keystrap release that wrote it
                    { "titles", titles },
                    { "places", placeUniverse.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => (object)kv.Value) },
                    { "sessions", sessions.Select(s => new Dictionary<string, object>
                        { { "u", s.Universe }, { "p", s.Place }, { "job", s.JobId }, { "start", Ms(s.Start) }, { "end", Ms(s.End) } }).ToList() },
                };
                Directory.CreateDirectory(Paths.Data);
                string tmp = StoreFile + ".tmp";
                File.WriteAllText(tmp, json.Serialize(data));
                if (File.Exists(StoreFile)) File.Replace(tmp, StoreFile, null);
                else File.Move(tmp, StoreFile);
            }
            catch (Exception ex)
            {
                Log.Write("Saving playtime failed: " + ex.Message);
            }
        }
    }

    // Drawn natively over the WebView while it starts (that can take seconds on a cold start, during which the page
    // can't draw anything): the Keystrap mark, a spinning arc and a status line. Removed once the page has its data.
    class LoadingOverlay : Control
    {
        static readonly Color Accent = Color.FromArgb(0xff, 0xfb, 0xa6);
        readonly System.Windows.Forms.Timer spin = new System.Windows.Forms.Timer { Interval = 16 };
        float angle;
        string status = "Starting…";

        public LoadingOverlay(Color background)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = background;
            Dock = DockStyle.Fill;
            spin.Tick += (s, e) => { angle = (angle + 6) % 360; Invalidate(SpinnerBounds()); };
            spin.Start();
        }

        public string Status
        {
            set { status = value; Invalidate(); Log.Write("Startup: " + value); }
        }

        Point Centre() { return new Point(ClientSize.Width / 2, ClientSize.Height / 2 - 30); }

        Rectangle SpinnerBounds()
        {
            var c = Centre();
            return new Rectangle(c.X - 16, c.Y + 62, 32, 32);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var c = Centre();

            // The mark: same geometry as the app logo, 72 px.
            float k = 72f / 1024f;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath { FillMode = System.Drawing.Drawing2D.FillMode.Alternate })
            {
                path.AddRectangle(new RectangleF(128 * k, 128 * k, 768 * k, 768 * k));
                path.AddRectangle(new RectangleF(402 * k, 402 * k, 220 * k, 220 * k));
                using (var m = new System.Drawing.Drawing2D.Matrix())
                {
                    m.Translate(c.X - 36, c.Y - 36);
                    m.RotateAt(15, new PointF(512 * k, 512 * k));
                    path.Transform(m);
                }
                using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Point(0, c.Y - 36), new Point(0, c.Y + 37),
                    Color.FromArgb(0xff, 0xfd, 0xd0), Color.FromArgb(0xff, 0xf7, 0x99)))
                    g.FillPath(brush, path);
            }

            var sb = SpinnerBounds();
            sb.Inflate(-4, -4);
            using (var track = new Pen(Color.FromArgb(0x2e, 0x2e, 0x33), 3f))
                g.DrawEllipse(track, sb);
            using (var arc = new Pen(Accent, 3f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                g.DrawArc(arc, sb, angle, 90);

            using (var font = new Font("Segoe UI", 10f))
            using (var brush = new SolidBrush(Color.FromArgb(0xb4, 0xb4, 0xbc)))
            {
                var fmt = new StringFormat { Alignment = StringAlignment.Center };
                g.DrawString(status, font, brush, new RectangleF(0, c.Y + 108, ClientSize.Width, 24), fmt);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) spin.Dispose();
            base.Dispose(disposing);
        }
    }

    class MainForm : Form
    {
        const int WM_NCLBUTTONDOWN = 0xA1;
        const int HTCAPTION = 0x2;
        const int WM_NCHITTEST = 0x84;
        const int RESIZE_BORDER = 4;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        WebView2 web = new WebView2();   // replaced once if a bundled runtime fails and the system one is tried instead
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        bool launching;
        bool skyBusy;
        bool sendingSuggestion;
        bool playtimeReady;
        readonly DiscordPresence discord = new DiscordPresence();
        readonly DateTime startedUtc = DateTime.UtcNow;
        string presenceProfile;   // active profile name, shown while browsing the launcher
        readonly System.Windows.Forms.Timer playtimeTimer = new System.Windows.Forms.Timer();
        PlaytimeTracker playtime;
        Mutex robloxSingleton;     // held while multi-instance is on, so Roblox allows extra windows
        int lastProgressTick;
        string lastProgressText;

        string pendingLink;   // a website link waiting for the page to finish loading
        bool pageReady;
        LoadingOverlay loading;   // null once the page is showing

        // From any thread.
        void LoadingStatus(string text)
        {
            if (InvokeRequired) { try { BeginInvoke((Action)(() => LoadingStatus(text))); } catch (InvalidOperationException) { } return; }
            if (loading != null) loading.Status = text;
        }

        void HideLoading()
        {
            if (loading == null) return;
            Controls.Remove(loading);
            loading.Dispose();
            loading = null;
        }

        public MainForm(string link)
        {
            pendingLink = link;
            Load += (s, e) => LinkPipe.Listen(message => BeginInvoke((Action)(() => OnLinkHandoff(message))));
            Text = "Keystrap";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1080, 700);
            MinimumSize = new Size(820, 560);
            BackColor = Color.FromArgb(0x1c, 0x1c, 0x1f);
            Padding = new Padding(RESIZE_BORDER);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // The window is opaque from the first frame, painted the same colour as the page's background; the page
            // fades its own content in once it has rendered. (A transparent window could leave WebView2 believing
            // it was hidden, so it never drew: the "black screen" at startup.)
            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = BackColor;
            Controls.Add(web);
            // Covers the WebView until the page has drawn with its data; it's the only thing on screen until then,
            // so it also moves the window (the page's title bar isn't there yet).
            loading = new LoadingOverlay(BackColor);
            loading.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            };
            Controls.Add(loading);
            loading.BringToFront();
            Load += async (s, e) => await InitWebAsync();

            // Playtime is tracked for as long as Keystrap is open, whichever launcher started Roblox.
            // Its history is read from disk in the background (see LoadPlaytimeAsync) before polling starts.
            playtime = new PlaytimeTracker(() => { Post(playtime.Snapshot()); UpdatePresence(); });

            discord.StatusChanged += (state, detail) =>
            {
                try { BeginInvoke((Action)(() => Post(new Dictionary<string, object> { { "type", "discord-status" }, { "state", state }, { "detail", detail } }))); }
                catch (InvalidOperationException) { }   // window closing
            };
            FormClosed += (s, e) => discord.Dispose();
            playtimeTimer.Interval = 3000;
            playtimeTimer.Tick += (s, e) =>
            {
                try { playtime.Poll(); }
                catch (Exception ex) { Log.Write("Playtime poll failed: " + ex.Message); }
            };
            Load += (s, e) => LoadPlaytimeAsync();
        }

        async void LoadPlaytimeAsync()
        {
            try { await Task.Run(() => playtime.LoadFromDisk()); }
            catch (Exception ex) { Log.Write("Loading playtime failed: " + ex.Message); }
            playtime.ResolveMissingTitles();
            playtimeReady = true;
            playtimeTimer.Start();
            if (pageReady) Post(playtime.Snapshot());
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2, dark = 1;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x00020000; // WS_MINIMIZEBOX: lets the taskbar button minimize a borderless window
                return cp;
            }
        }

        async Task InitWebAsync()
        {
            byte[] page;
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("keystrap.html"))
            using (var buf = new MemoryStream())
            {
                // keystrap.html is a page fragment (the artifact host adds the skeleton), so add a doctype here.
                // The file declares its own UTF-8 charset on its first line.
                byte[] head = System.Text.Encoding.UTF8.GetBytes("<!doctype html>\n");
                buf.Write(head, 0, head.Length);
                res.CopyTo(buf);
                page = buf.ToArray();
            }

            LoadingStatus("Initializing WebView2…");
            try
            {
                // The window starts fully transparent (see Reveal); Chromium's occlusion tracking can then decide the page
                // is hidden and stop drawing it, leaving a blank window. Keystrap is always on screen when open, so turn it off.
                EmbeddedLibs.UseLoader();
                var options = new CoreWebView2EnvironmentOptions("--disable-features=CalculateNativeWinOcclusion");
                string fixedRuntime = FindFixedRuntime();
                Exception bundledFailure = null;
                try
                {
                    var env = await CoreWebView2Environment.CreateAsync(fixedRuntime, Path.Combine(Paths.Data, "WebView2"), options);
                    await web.EnsureCoreWebView2Async(env);
                }
                catch (Exception ex)
                {
                    if (fixedRuntime == null) throw;
                    bundledFailure = ex;
                }
                if (bundledFailure != null)
                {
                    // A bundled runtime that won't start (damaged, or unreadable to WebView2's sandbox): use the one
                    // installed with Windows. A WebView2 control can't be initialised twice, so swap in a fresh one.
                    Log.Write("The bundled WebView2 runtime at " + fixedRuntime + " failed (" + bundledFailure.Message + "); trying the system runtime.");
                    var fresh = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
                    Controls.Remove(web);
                    web.Dispose();
                    web = fresh;
                    Controls.Add(web);
                    if (loading != null) loading.BringToFront();
                    var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.Data, "WebView2"), options);
                    await web.EnsureCoreWebView2Async(env);
                }
            }
            catch (Exception ex)
            {
                Log.Write("WebView2 failed to start: " + ex);
                bool missing = ex is WebView2RuntimeNotFoundException || ex is FileNotFoundException;
                MessageBox.Show(missing
                        ? "Keystrap couldn't start the Microsoft Edge WebView2 Runtime. It is missing or damaged on this PC.\r\n\r\n" +
                          "Repair it in Settings > Apps > Installed apps > Microsoft Edge WebView2 Runtime > Modify, " +
                          "or install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703, then start Keystrap again."
                        : "Keystrap couldn't start its window: " + ex.Message,
                    "Keystrap", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            var core = web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            // SmartScreen looked up keystrap.app online before showing the built-in page: about 2 s on every start.
            // The page is served from inside the exe, and links to real websites open in the user's browser, not here.
            core.Settings.IsReputationCheckingRequired = false;
            // Serve the embedded page from memory on a fixed origin, so localStorage persists between runs.
            core.AddWebResourceRequestedFilter("https://keystrap.app/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (s, e) =>
            {
                string path = new Uri(e.Request.Uri).AbsolutePath;
                e.Response = path == "/" || path == "/index.html"
                    ? core.Environment.CreateWebResourceResponse(new MemoryStream(page), 200, "OK", "Content-Type: text/html; charset=utf-8")
                    : core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            };
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                Process.Start(e.Uri);
            };
            LoadingStatus("Loading UI components…");
            // Timing marks in the log, to see where startup time goes.
            core.ContentLoading += (s, e) => Log.Write("Startup: page content loading");
            core.DOMContentLoaded += (s, e) => Log.Write("Startup: page DOM ready");
            core.Navigate("https://keystrap.app/index.html");

            // If the page never reports in (a script error, say), don't leave the spinner up forever.
            await Task.Delay(20000);
            if (loading != null) { Log.Write("The page didn't report ready within 20 s; showing it anyway."); HideLoading(); }
        }

        // A Fixed Version WebView2 runtime, if one is shipped next to the exe (WebView2Runtime\)
        // or unpacked into %LocalAppData%\Keystrap\Runtime. Null falls back to the installed Evergreen runtime.
        // The runtime must match this process's bitness; unpacked cabs end in ".x64" / ".x86".
        string FindFixedRuntime()
        {
            string otherArch = Environment.Is64BitProcess ? ".x86" : ".x64";
            string[] roots = { Path.Combine(Application.StartupPath, "WebView2Runtime"), Path.Combine(Paths.Data, "Runtime") };
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                if (File.Exists(Path.Combine(root, "msedgewebview2.exe"))) { GrantSandboxRead(root); return root; }
                foreach (string sub in Directory.GetDirectories(root))
                    if (!sub.EndsWith(otherArch, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(sub, "msedgewebview2.exe"))) { GrantSandboxRead(root); return sub; }
            }
            return null;
        }

        // WebView2's sandboxed processes run as "all application packages"; a Fixed Version runtime they can't read
        // never starts (Microsoft's guidance is to grant this). Added once, inherited by everything inside.
        static void GrantSandboxRead(string folder)
        {
            try
            {
                var dir = new DirectoryInfo(folder);
                var acl = dir.GetAccessControl();
                bool changed = false;
                foreach (string sid in new[] { "S-1-15-2-1", "S-1-15-2-2" })
                {
                    var rule = new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid),
                        System.Security.AccessControl.FileSystemRights.ReadAndExecute,
                        System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                        System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow);
                    bool has = acl.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>()
                        .Any(r => r.IdentityReference.Value == sid && (r.FileSystemRights & System.Security.AccessControl.FileSystemRights.ReadAndExecute) == System.Security.AccessControl.FileSystemRights.ReadAndExecute);
                    if (!has) { acl.AddAccessRule(rule); changed = true; }
                }
                if (changed) { dir.SetAccessControl(acl); Log.Write("Gave WebView2's sandbox read access to " + folder); }
            }
            catch (Exception ex) { Log.Write("Couldn't set access on " + folder + ": " + ex.Message); }
        }

        // ---------- page <-> app messages ----------

        void Post(object message)
        {
            if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json.Serialize(message));
        }

        void Toast(string text)
        {
            Post(new Dictionary<string, object> { { "type", "toast" }, { "text", text } });
        }

        static object VersionInfo(RobloxVersion v)
        {
            if (v == null) return null;
            return new Dictionary<string, object> { { "version", v.Version }, { "guid", v.Guid } };
        }

        static bool Bool(Dictionary<string, object> m, string key)
        {
            object v;
            return m.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        static int Int(Dictionary<string, object> m, string key)
        {
            object v;
            if (!m.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        const string FpsFlag = "DFIntTaskSchedulerTargetFps";
        const int UncappedFps = 9999;
        const int MenuFps = 60;

        // The frame rate this launch starts Roblox at, or 0 to leave Roblox's own setting alone. Roblox reads it once at
        // startup, so it holds for the whole session: joining a game from inside the app home stays at the menu cap.
        // toAppHome: no game link, so Roblox opens on its home screen.
        static int LaunchFps(Dictionary<string, object> msg, bool toAppHome)
        {
            if (toAppHome && Bool(msg, "menuCap")) return MenuFps;
            if (!Bool(msg, "unlockFps")) return 0;
            int target = Int(msg, "fpsTarget");   // 0 means uncapped; otherwise the cap the user picked
            return target > 0 ? target : UncappedFps;
        }

        void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> msg;
            try { msg = json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); }
            catch { return; }
            object type;
            if (msg == null || !msg.TryGetValue("type", out type)) return;

            switch (type as string)
            {
                case "drag":
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                    break;
                case "min":
                    WindowState = FormWindowState.Minimized;
                    break;
                case "max":
                    if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
                    else
                    {
                        MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
                        WindowState = FormWindowState.Maximized;
                    }
                    break;
                case "close":
                    Close();
                    break;
                case "ready":
                    OnPageReadyAsync();
                    break;
                case "webhook-test":
                    if (Program.DevMode) SendTestNotificationsAsync();
                    break;
                case "save":
                    SaveState(msg.ContainsKey("state") ? msg["state"] : null);
                    break;
                case "launch":
                    LaunchAsync(msg);
                    break;
                case "launch-official":
                    try { Process.Start(new ProcessStartInfo("roblox-player://") { UseShellExecute = true }); }
                    catch (Exception ex)
                    {
                        Log.Write("roblox-player:// failed: " + ex.Message);
                        Toast("The Roblox app isn't installed on this PC.");
                    }
                    break;
                case "open-logs":
                    OpenLogs();
                    break;
                case "clear-versions":
                    ClearVersions();
                    break;
                case "skies":
                    SendSkiesAsync();
                    break;
                case "sky-apply":
                    ApplySkyAsync(msg.ContainsKey("id") ? msg["id"] as string : null);
                    break;
                case "sky-import":
                    ImportSky();
                    break;
                case "sky-delete":
                    try { Skies.Delete(msg["id"] as string); }
                    catch (Exception ex) { Log.Write("Deleting a sky failed: " + ex.Message); Toast("Couldn't delete that sky. Details are in the log."); }
                    SendSkiesAsync();
                    break;
                case "discord":
                    presenceProfile = msg.ContainsKey("profile") ? msg["profile"] as string : null;
                    CrashReporter.Profile = presenceProfile;
                    if (msg.ContainsKey("crashReports")) CrashReporter.Enabled = Bool(msg, "crashReports");
                    discord.Configure(Bool(msg, "on"), msg.ContainsKey("appId") ? (msg["appId"] as string ?? "").Trim() : "");
                    UpdatePresence();
                    break;
                case "protocol":
                    SetProtocol(Bool(msg, "on"));
                    break;
                case "suggest":
                    SuggestAsync(msg.ContainsKey("text") ? msg["text"] as string : null);
                    break;
            }
        }

        // ---------- Discord Rich Presence ----------

        static long UnixSeconds(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }

        // Discord limits these fields to 2-128 characters.
        static string Field(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            if (s.Length < 2) s = s + " ";
            return s.Length > 128 ? s.Substring(0, 127) + "…" : s;
        }

        // Builds the card from what Keystrap knows right now: the game Playtime sees in Roblox's logs, or the launcher.
        void UpdatePresence()
        {
            var a = new Dictionary<string, object>();
            var assets = new Dictionary<string, object>();
            PlaytimeTracker.LiveGame game = playtime.CurrentGame();
            if (game != null)
            {
                RobloxGames.Info info = RobloxGames.Get(game.Universe, UpdatePresence);   // re-runs once the creator and icon arrive
                string title = game.Title ?? "a Roblox game";
                a["details"] = Field("Playing " + title);
                a["state"] = Field(info != null && info.Creator != null ? "by " + info.Creator : "Playing Roblox");
                a["timestamps"] = new Dictionary<string, object> { { "start", UnixSeconds(game.Start) } };
                assets["large_image"] = info != null && info.Icon != null ? info.Icon : "keystrap";
                assets["large_text"] = Field(title);
                assets["small_image"] = "keystrap";
                assets["small_text"] = "Keystrap " + AppInfo.Version;
                a["buttons"] = new object[]
                {
                    new Dictionary<string, object> { { "label", "See game page" }, { "url", "https://www.roblox.com/games/" + game.Place } },
                };
            }
            else
            {
                a["details"] = Field("Browsing Keystrap (" + AppInfo.Version + ")");
                if (!string.IsNullOrEmpty(presenceProfile)) a["state"] = Field("Profile: " + presenceProfile);
                a["timestamps"] = new Dictionary<string, object> { { "start", UnixSeconds(startedUtc) } };
                assets["large_image"] = "keystrap";
                assets["large_text"] = "Keystrap " + AppInfo.Version;
            }
            a["assets"] = assets;
            discord.SetActivity(a);
        }

        // ---------- Custom Skies ----------

        static string InstalledDir()
        {
            RobloxVersion v = Bootstrapper.GetInstalled();
            return v == null ? null : Path.Combine(Paths.Versions, v.Guid);
        }

        async void SendSkiesAsync()
        {
            try
            {
                string dir = InstalledDir();
                var items = await Task.Run(() => Skies.List(dir));
                Post(new Dictionary<string, object> { { "type", "skies" }, { "items", items } });
            }
            catch (Exception ex) { Log.Write("Listing skies failed: " + ex); }
        }

        void SkyProgress(string text)
        {
            try { BeginInvoke((Action)(() => Post(new Dictionary<string, object> { { "type", "sky-progress" }, { "text", text } }))); }
            catch (InvalidOperationException) { }
        }

        // Renders the sky if it's a preset seen for the first time, then puts it into the installed Roblox straight away.
        async void ApplySkyAsync(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (skyBusy || launching)
            {
                Post(new Dictionary<string, object> { { "type", "sky-applied" }, { "id", id }, { "ok", false }, { "message", "Wait for the current launch or sky change to finish." } });
                return;
            }
            skyBusy = true;
            try
            {
                string dir = InstalledDir();
                await Task.Run(() =>
                {
                    Skies.Prepare(id, SkyProgress);
                    if (dir != null) Skies.ApplyTo(dir, id, SkyProgress);
                });
                Process[] running = Process.GetProcessesByName("RobloxPlayerBeta");
                bool robloxOpen = running.Length > 0;
                foreach (Process p in running) p.Dispose();
                string note = dir == null ? "It's applied when Keystrap installs Roblox on your next launch."
                    : robloxOpen ? "Restart Roblox to see it." : null;
                Post(new Dictionary<string, object> { { "type", "sky-applied" }, { "id", id }, { "ok", true }, { "note", note } });
                SendSkiesAsync();   // the preset now has its files (and the default sky its preview)
            }
            catch (Exception ex)
            {
                Log.Write("Applying sky " + id + " failed: " + ex);
                string message = ex is InvalidOperationException ? ex.Message
                    : ex is IOException || ex is UnauthorizedAccessException ? "Roblox is using its sky files. Close Roblox, then try again."
                    : "Keystrap couldn't apply that sky. Details are in the log.";
                Post(new Dictionary<string, object> { { "type", "sky-applied" }, { "id", id }, { "ok", false }, { "message", message } });
            }
            finally
            {
                skyBusy = false;
            }
        }

        void ImportSky()
        {
            string[] files;
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Choose the six sky images";
                dlg.Multiselect = true;
                dlg.Filter = "Sky images and textures|*.png;*.jpg;*.jpeg;*.bmp;*.tex;*.dds;*.rbxm;*.rbxmx|All files|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                files = dlg.FileNames;
            }
            ImportSkyAsync(files);
        }

        async void ImportSkyAsync(string[] files)
        {
            Post(new Dictionary<string, object> { { "type", "sky-progress" }, { "text", "Importing your sky…" } });
            try
            {
                string id = await Task.Run(() => Skies.Import(files));
                Post(new Dictionary<string, object> { { "type", "sky-imported" }, { "id", id } });
                SendSkiesAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Importing a sky failed: " + ex.Message);
                string message = ex is InvalidOperationException ? ex.Message : "Keystrap couldn't import those files. Details are in the log.";
                Post(new Dictionary<string, object> { { "type", "sky-import-error" }, { "message", message } });
            }
        }

        // A second Keystrap start (usually a Play button on roblox.com) sent us its link, or asked to be shown.
        void OnLinkHandoff(string message)
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            if (message == LinkPipe.Activate || !RobloxLink.IsLink(message)) return;
            Log.Write("Received a Roblox link from the website (" + RobloxLink.Describe(message) + ")");
            if (pageReady) Post(new Dictionary<string, object> { { "type", "launch-link" }, { "link", message } });
            else pendingLink = message;
        }

        // The page sends this at every start, so the registration follows the setting and Keystrap's current location,
        // and wins back the links if Roblox's own installer took them.
        async void SetProtocol(bool on)
        {
            bool registered = false;
            try
            {
                registered = await Task.Run(() =>
                {
                    if (on) Protocol.Register(); else Protocol.Unregister();
                    return Protocol.IsRegistered();
                });
            }
            catch (Exception ex)
            {
                Log.Write("Updating the Roblox link handler failed: " + ex.Message);
                Toast("Keystrap couldn't change which app opens Roblox links. Details are in the log.");
            }
            Post(new Dictionary<string, object> { { "type", "protocol-status" }, { "registered", registered } });
        }

        async void SuggestAsync(string text)
        {
            if (sendingSuggestion) return;
            sendingSuggestion = true;
            try
            {
                string raw = (text ?? "").Trim();
                if (raw.Length < Feedback.MinLength)
                    throw new InvalidOperationException("Write a little more so the developers know what you mean.");
                if (raw.Length > Feedback.MaxLength) raw = raw.Substring(0, Feedback.MaxLength);

                Suggestion s = Feedback.Polish(raw);
                await Feedback.SendAsync(raw, s, Environment.UserName);
                Log.Write("Sent a suggestion (" + s.Category + ")");
                Post(new Dictionary<string, object>
                {
                    { "type", "suggest-done" },
                    { "category", s.Category },
                    { "summary", s.Summary },
                    { "details", s.Details },
                    { "benefit", s.Benefit },
                });
            }
            catch (Exception ex)
            {
                Log.Write("Sending suggestion failed: " + ex);
                string message = ex is HttpRequestException || ex is TaskCanceledException
                    ? "Couldn't reach the suggestion box. Check your internet connection and try again."
                    : ex is InvalidOperationException
                        ? ex.Message
                        : "Keystrap couldn't send your suggestion. Details are in the log.";
                Post(new Dictionary<string, object> { { "type", "suggest-error" }, { "message", message } });
            }
            finally
            {
                sendingSuggestion = false;
            }
        }

        // The page has loaded: read settings, the refresh rate and the Roblox install on a background thread,
        // so the UI thread is free to draw, then hand everything to the page in one message.
        async void OnPageReadyAsync()
        {
            Dictionary<string, object> init = await Task.Run(() =>
            {
                LoadingStatus("Reading your settings…");
                object state = LoadState();
                LoadingStatus("Checking your display…");
                int refresh = Display.PrimaryRefreshRate();
                LoadingStatus("Checking your Roblox install…");
                object installed = VersionInfo(Bootstrapper.GetInstalled());
                return new Dictionary<string, object>
                {
                    { "type", "init" },
                    { "state", state },
                    { "refreshRate", refresh },
                    { "dataDir", Paths.Data },
                    { "versionsBytes", null }, // filled in by SendVersionsSizeAsync; scanning the Roblox install is slow
                    { "installed", installed },
                    { "dev", Program.DevMode },   // shows the developer tools in Settings
                };
            });
            Post(init);
            pageReady = true;
            LoadingStatus("Ready");
            await Task.Delay(120);   // a couple of frames for the page to draw what init just gave it
            HideLoading();
            if (pendingLink != null) { Post(new Dictionary<string, object> { { "type", "launch-link" }, { "link", pendingLink } }); pendingLink = null; }
            if (playtimeReady) Post(playtime.Snapshot());
            SendSkiesAsync();
            SendVersionsSizeAsync();
            CheckRobloxAsync();
        }

        // One sample per category, spaced out so Discord's webhook rate limit (5 per 2 s) isn't hit.
        async void SendTestNotificationsAsync()
        {
            string version = AppInfo.Version;
            Exception sample;
            try { throw new InvalidOperationException("This is a test crash from Settings → Test notifications."); }
            catch (Exception ex) { sample = ex; }
            var tests = new[]
            {
                new Notification(NotificationKind.MinorUpdate, "Test · " + version + " polish", "Small improvements.")
                    .Field(version + " Update", "Smoother loading screen and tidier Settings."),
                new Notification(NotificationKind.MajorUpdate, "Test · Keystrap v2", "An installed copy of Keystrap updated itself.")
                    .Field("v2 Update", "Installed over " + version + " from Keystrap.zip.")
                    .Field("Windows", Environment.OSVersion.Version.ToString()),
                new Notification(NotificationKind.MinorFix, "Test · " + version + " fix", "A small fix.")
                    .Field(version + " Fix", "Profile colours no longer reset after a restart."),
                new Notification(NotificationKind.MajorFix, "Test · " + version + " launch fix", "A fix for an issue that stopped Roblox from launching.")
                    .Field(version + " Fix", "Roblox starts again when the protocol handler was missing."),
                CrashReporter.BuildNotification(sample, "Test notification", false),
                CrashReporter.BuildNotification(sample, "Test notification", true),
            };
            int sent = 0;
            foreach (var n in tests)
            {
                if (await WebhookService.SendAsync(n)) sent++;
                else Log.Write("Test notification (" + WebhookService.Label(n.Kind) + ") failed: " + WebhookService.LastError);
                await Task.Delay(700);
            }
            Log.Write("Test notifications: " + sent + " of " + tests.Length + " sent");
            Post(new Dictionary<string, object> { { "type", "webhook-test-done" }, { "sent", sent }, { "total", tests.Length }, { "error", sent < tests.Length ? WebhookService.LastError : null } });
        }

        async void SendVersionsSizeAsync()
        {
            long bytes = await Task.Run(() => Bootstrapper.VersionsSize());
            Post(new Dictionary<string, object>
            {
                { "type", "versions-size" },
                { "bytes", bytes },
                { "installed", VersionInfo(Bootstrapper.GetInstalled()) },
            });
        }

        async void CheckRobloxAsync()
        {
            RobloxVersion latest = null;
            string error = null;
            try { latest = await Bootstrapper.GetLatestAsync(); }
            catch (Exception ex)
            {
                error = "Couldn't reach Roblox to check for updates.";
                Log.Write("Version check failed: " + ex.Message);
            }
            Post(new Dictionary<string, object>
            {
                { "type", "roblox-status" },
                { "installed", VersionInfo(Bootstrapper.GetInstalled()) },
                { "latest", VersionInfo(latest) },
                { "error", error },
            });
        }

        object LoadState()
        {
            try
            {
                if (File.Exists(Paths.State)) return json.DeserializeObject(File.ReadAllText(Paths.State));
            }
            catch (Exception ex)
            {
                Log.Write("settings.json couldn't be read, starting fresh: " + ex.Message);
                try { File.Copy(Paths.State, Paths.State + ".bad", true); } catch { }
            }
            return null;
        }

        void SaveState(object state)
        {
            if (state == null) return;
            try
            {
                Directory.CreateDirectory(Paths.Data);
                string tmp = Paths.State + ".tmp";
                File.WriteAllText(tmp, json.Serialize(state));
                if (File.Exists(Paths.State)) File.Replace(tmp, Paths.State, null);
                else File.Move(tmp, Paths.State);
            }
            catch (Exception ex)
            {
                Log.Write("Saving settings failed: " + ex.Message);
                Toast("Keystrap couldn't save your settings. Details are in the log.");
            }
        }

        void OpenLogs()
        {
            try
            {
                Directory.CreateDirectory(Paths.Logs);
                Process.Start("explorer.exe", "\"" + Paths.Logs + "\"");
            }
            catch (Exception ex)
            {
                Log.Write("Opening logs folder failed: " + ex.Message);
                Toast("Couldn't open the logs folder: " + ex.Message);
            }
        }

        void ClearVersions()
        {
            if (launching) { Toast("Wait for the launch to finish first."); return; }
            try
            {
                if (Directory.Exists(Paths.Versions)) Directory.Delete(Paths.Versions, true);
                if (Directory.Exists(Paths.Downloads)) Directory.Delete(Paths.Downloads, true);
                Log.Write("Cleared downloaded versions");
                Toast("Downloaded Roblox versions cleared");
            }
            catch (Exception ex)
            {
                Log.Write("Clearing versions failed: " + ex.Message);
                Toast("Some files are in use. Close Roblox, then try again.");
            }
            Post(new Dictionary<string, object>
            {
                { "type", "versions-size" },
                { "bytes", Bootstrapper.VersionsSize() },
                { "installed", VersionInfo(Bootstrapper.GetInstalled()) },
            });
        }

        // Called from the download thread; forwards at most ~8 updates a second to the page.
        void ReportProgress(string text, double pct)
        {
            int now = Environment.TickCount;
            if (text == lastProgressText && now - lastProgressTick < 120) return;
            lastProgressTick = now;
            lastProgressText = text;
            Action post = () => { if (launching) Post(new Dictionary<string, object> { { "type", "launch-progress" }, { "text", text }, { "pct", pct } }); };
            // Post directly on the UI thread, so a queued update can't land after "launch-done".
            if (!InvokeRequired) { post(); return; }
            try { BeginInvoke(post); }
            catch (InvalidOperationException) { } // window closing
        }

        async void LaunchAsync(Dictionary<string, object> msg)
        {
            if (launching) return;
            launching = true;
            try
            {
                var flags = new Dictionary<string, string>();
                object raw;
                if (msg.TryGetValue("flags", out raw) && raw is Dictionary<string, object>)
                    foreach (var kv in (Dictionary<string, object>)raw)
                        flags[kv.Key] = Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture);

                // The frame rate goes into Roblox's Maximum Frame Rate, the setting Roblox actually honours, and into the
                // flag as well in case Roblox allows it again; the flag wins over the same one in a profile. Both are
                // written before Roblox starts, since that's the only time it reads them. Nothing to set: Roblox keeps
                // its own limit (ClientAppSettings.json is rewritten every launch).
                string link = msg.ContainsKey("link") ? msg["link"] as string : null;
                bool toAppHome = !RobloxLink.IsLink(link);
                int fps = LaunchFps(msg, toAppHome);
                if (fps > 0)
                {
                    string value = fps.ToString(CultureInfo.InvariantCulture);
                    string fromProfile;
                    if (flags.TryGetValue(FpsFlag, out fromProfile) && fromProfile != value)
                        Log.Write("Frame rate setting replaces the profile's " + FpsFlag + " (" + fromProfile + ")");
                    flags[FpsFlag] = value;
                    Log.Write(toAppHome && Bool(msg, "menuCap") ? "Cap menu: opening the Roblox app home at " + fps + " FPS"
                        : fps == UncappedFps ? "Unlock FPS: uncapped" : "Unlock FPS: capped at " + fps);
                    Bootstrapper.SetFramerateCap(fps);
                }

                ReportProgress("Checking for Roblox updates…", 0.02);
                RobloxVersion latest = null;
                try { latest = await Bootstrapper.GetLatestAsync(); }
                catch (Exception ex) { Log.Write("Version check failed, using the installed copy: " + ex.Message); }
                RobloxVersion version = latest ?? Bootstrapper.GetInstalled();
                if (version == null)
                    throw new InvalidOperationException("Couldn't reach Roblox to download it. Check your internet connection and try again.");

                string dir = await Task.Run(() => Bootstrapper.InstallAsync(version, ReportProgress));

                // The saved Custom Skies choice, re-applied every launch so freshly installed versions get it too.
                string sky = msg.ContainsKey("sky") ? msg["sky"] as string : null;
                if (!string.IsNullOrEmpty(sky))
                {
                    ReportProgress(sky == Skies.Default ? "Checking Roblox's sky…" : "Applying your sky…", 0.95);
                    try { await Task.Run(() => Skies.ApplyTo(dir, sky, text => ReportProgress(text, 0.95))); }
                    catch (Exception ex)
                    {
                        Log.Write("Applying the sky failed: " + ex.Message);
                        Toast("Keystrap couldn't apply your custom sky, so Roblox starts with its usual sky.");
                    }
                }

                ReportProgress("Applying flags…", 0.96);
                Bootstrapper.WriteClientSettings(dir, flags);

                bool multi = Bool(msg, "multiInstance");
                if (multi && robloxSingleton == null) robloxSingleton = new Mutex(true, "ROBLOX_singletonMutex");

                ReportProgress("Starting Roblox…", 0.99);
                Bootstrapper.Start(dir, toAppHome ? null : link);

                Post(new Dictionary<string, object> { { "type", "launch-done" } });
                Post(new Dictionary<string, object>
                {
                    { "type", "versions-size" },
                    { "bytes", Bootstrapper.VersionsSize() },
                    { "installed", VersionInfo(Bootstrapper.GetInstalled()) },
                });

                if (Bool(msg, "closeAfterLaunch"))
                {
                    if (robloxSingleton != null) WindowState = FormWindowState.Minimized; // closing would release the mutex
                    else Close();
                }
            }
            catch (Exception ex)
            {
                Log.Write("Launch failed: " + ex);
                string text = ex is HttpRequestException || ex is WebException || ex is TaskCanceledException
                    ? "Couldn't download Roblox. Check your internet connection and try again."
                    : ex is IOException || ex is UnauthorizedAccessException
                        ? "Couldn't write Roblox's files. Close Roblox if it's running, then try again."
                        : ex.Message;
                Post(new Dictionary<string, object> { { "type", "launch-error" }, { "message", text } });
            }
            finally
            {
                launching = false;
            }
        }

        // Resize from the thin frame around the web view.
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WM_NCHITTEST || WindowState == FormWindowState.Maximized) return;
            int lp = unchecked((int)m.LParam.ToInt64());
            var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            bool l = p.X < RESIZE_BORDER, r = p.X >= ClientSize.Width - RESIZE_BORDER;
            bool t = p.Y < RESIZE_BORDER, b = p.Y >= ClientSize.Height - RESIZE_BORDER;
            if (t && l) m.Result = (IntPtr)13;
            else if (t && r) m.Result = (IntPtr)14;
            else if (b && l) m.Result = (IntPtr)16;
            else if (b && r) m.Result = (IntPtr)17;
            else if (l) m.Result = (IntPtr)10;
            else if (r) m.Result = (IntPtr)11;
            else if (t) m.Result = (IntPtr)12;
            else if (b) m.Result = (IntPtr)15;
        }
    }
}
