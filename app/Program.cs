// Keystrap desktop app: a borderless WinForms window hosting keystrap.html in WebView2, plus the
// backend the page drives through chrome.webview messages: saved state, the Roblox installer and
// launcher, skies, crosshairs and mods, Discord presence, the Playtime tracker, crash reports and logs.
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

[assembly: AssemblyTitle("Keystrap")]
[assembly: AssemblyProduct("Keystrap")]
// The version attributes come from version.txt, through Version.g.cs (written by build.ps1).

namespace Keystrap
{
    static class AppInfo
    {
        // The release name used everywhere: logs, web requests and the data files. Set it in
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
        public static string UpdateFeed;   // --update-feed <file>: check this saved release list instead of GitHub, for testing

        [STAThread]
        static void Main(string[] args)
        {
            EmbeddedLibs.Install();   // first, before anything touches WebView2
            WebhookService.Source = "Keystrap " + AppInfo.Version;
            // The uninstaller runs "Keystrap.exe --unregister": hand roblox.com's Play links back to Roblox, then quit,
            // so they don't point at an exe that's about to be deleted.
            if (args.Any(a => string.Equals(a, "--unregister", StringComparison.OrdinalIgnoreCase)))
            {
                try { Protocol.Unregister(); } catch (Exception ex) { Log.Write("Unregister failed: " + ex.Message); }
                return;
            }
            DevMode = !Secrets.PublicBuild && args.Any(a => string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase));
            int feedArg = Array.FindIndex(args, a => string.Equals(a, "--update-feed", StringComparison.OrdinalIgnoreCase));
            if (feedArg >= 0 && feedArg + 1 < args.Length) UpdateFeed = args[feedArg + 1];
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
            new Preset { Id = "blue-sky", Name = "Realistic Blue Sky", Desc = "Clear daytime sky with a hazy horizon and a bright sun.", Fn = BlueSky },
            new Preset { Id = "void-black", Name = "Void Black", Desc = "Pure black in every direction.", Fn = (x, y, z, s) => new Rgb(0, 0, 0) },
        };

        // Presets that were removed. Their rendered files are deleted, and a saved choice of one means Roblox's own sky.
        static readonly string[] Retired = { "anime-sunset", "vaporwave" };

        // The sky to actually apply: a removed preset or a deleted import falls back to Roblox's default.
        public static string Resolve(string id)
        {
            if (string.IsNullOrEmpty(id) || id == Default) return Default;
            if (Presets.Any(p => p.Id == id)) return id;
            if (id.StartsWith("custom-") && Directory.Exists(Path.Combine(Dir, id))) return id;
            Log.Write("Sky " + id + " no longer exists; using Roblox's default sky");
            return Default;
        }

        // Clears out the rendered files of removed presets, once they're no longer needed.
        public static void RemoveRetired()
        {
            foreach (string id in Retired)
            {
                string dir = Path.Combine(Dir, id);
                if (!Directory.Exists(dir)) continue;
                try { Directory.Delete(dir, true); Log.Write("Removed the retired sky preset " + id); }
                catch (Exception ex) { Log.Write("Couldn't remove the retired sky preset " + id + ": " + ex.Message); }
            }
        }

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
            if (id == Default) { try { Directory.Delete(backup, true); } catch { } }   // originals restored: drop the backup so later launches don't re-copy it
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

    // Custom Crosshairs: the page draws the chosen crosshair twice, 64px and 32px, and Keystrap keeps both in its data
    // folder. Every launch turns them into each of Roblox's mouse cursors below, at that cursor's own size, after
    // backing up Roblox's originals once per version; with no crosshair saved, the originals go back. Roblox draws
    // each cursor centred on the mouse. Games that set their own mouse icon keep theirs.
    static class Crosshairs
    {
        public static readonly string Dir = Path.Combine(Paths.Data, "Crosshair");
        const string BackupFolder = @"content\textures\keystrap-original-cursors";   // mirrors the paths below
        const string FarFile = "ArrowFarCursor.png", LockedFile = "MouseLockedCursor.png";
        static readonly object gate = new object();

        // Every mouse cursor Roblox shows players. Studio's building-tool cursors (Anchor, Weld and so on) stay as they are.
        static readonly string[] Cursors =
        {
            @"content\textures\Cursors\KeyboardMouse\ArrowFarCursor.png",   // over the game world
            @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png",      // over buttons and things you can click
            @"content\textures\Cursors\KeyboardMouse\IBeamCursor.png",      // over text boxes
            @"content\textures\ArrowFarCursor.png",                          // older copies of the three above,
            @"content\textures\ArrowCursor.png",                             // which Roblox still ships
            @"content\textures\IBeamCursor.png",
            @"content\textures\MouseLockedCursor.png",                       // Shift Lock and first person
            @"content\textures\Cursors\DragDetector\HoverCursor.png",        // over things you can drag
            @"content\textures\Cursors\DragDetector\ActivatedCursor.png",    // while dragging
        };

        // Keystrap 1.0.12 and 1.0.13 backed up only these two, by file name alone, straight into the backup folder.
        static readonly string[] FlatBackups = { Cursors[0], Cursors[6] };

        static bool IsCustom { get { return File.Exists(Path.Combine(Dir, FarFile)) && File.Exists(Path.Combine(Dir, LockedFile)); } }

        // Keeps the page's two PNGs (base64, 64px and 32px) as the crosshair to use, or clears it when either is null.
        public static void Save(string far, string locked)
        {
            lock (gate)
            {
                if (far == null || locked == null)
                {
                    if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
                    return;
                }
                Directory.CreateDirectory(Dir);
                WritePng(FarFile, far, 64);
                WritePng(LockedFile, locked, 32);
            }
        }

        static void WritePng(string name, string base64, int size)
        {
            byte[] bytes = Convert.FromBase64String(base64);
            using (var ms = new MemoryStream(bytes))
            using (Image img = Image.FromStream(ms))
                if (img.Width != size || img.Height != size) throw new InvalidDataException(name + " should be " + size + "px square.");
            string path = Path.Combine(Dir, name), temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        // Puts the saved crosshair into every cursor of a Roblox version folder, or Roblox's own back when none is saved.
        public static void ApplyTo(string versionDir)
        {
            lock (gate)
            {
                string backup = Path.Combine(versionDir, BackupFolder);
                bool custom = IsCustom;
                if (!custom && !Directory.Exists(backup)) return;   // nothing has been replaced in this version
                int done = 0;
                Size farSize = Size.Empty;   // the game-world cursor's size; Shift Lock reuses it so it doesn't shrink
                foreach (string cursor in Cursors)
                {
                    string target = Path.Combine(versionDir, cursor);
                    if (!File.Exists(target)) continue;   // not every Roblox version has every cursor
                    string original = OriginalOf(backup, cursor);
                    if (custom && !File.Exists(original))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(original));
                        File.Copy(target, original);
                    }
                    if (!File.Exists(original)) continue;
                    if (custom)
                    {
                        string name = Path.GetFileName(cursor);
                        Size own = SizeOf(original);
                        if (name == FarFile && farSize == Size.Empty) farSize = own;   // ArrowFarCursor comes before MouseLockedCursor
                        // Shift Lock and first person use MouseLockedCursor, which Roblox ships small. Draw the crosshair
                        // there at the game-world cursor's size instead, so it keeps the same size and doesn't shrink.
                        Size size = name == LockedFile && farSize != Size.Empty ? farSize : own;
                        File.WriteAllBytes(target, Sized(Path.Combine(Dir, size.Width <= 32 && size.Height <= 32 ? LockedFile : FarFile), size));
                    }
                    else File.Copy(original, target, true);
                    done++;
                }
                if (!custom && done > 0) { try { Directory.Delete(backup, true); } catch { } }   // originals restored: drop the backup so later launches don't re-copy it
                Log.Write((custom ? "Applied the custom crosshair to " : "Put Roblox's own cursors back in ") + done + " cursors in " + versionDir);
            }
        }

        // Where a cursor's original is kept, moving a backup from Keystrap 1.0.12 or 1.0.13 into place first.
        static string OriginalOf(string backup, string cursor)
        {
            string original = Path.Combine(backup, cursor);
            string flat = Path.Combine(backup, Path.GetFileName(cursor));
            if (!File.Exists(original) && FlatBackups.Contains(cursor) && File.Exists(flat))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(original));
                File.Move(flat, original);
            }
            return original;
        }

        static Size SizeOf(string file)
        {
            using (var ms = new MemoryStream(File.ReadAllBytes(file)))
            using (Image img = Image.FromStream(ms))
                return img.Size;
        }

        // A drawn crosshair as PNG bytes at a cursor's own size: unchanged when it already fits, otherwise scaled to fit
        // and centred, so the middle of the crosshair stays on the mouse.
        static byte[] Sized(string source, Size size)
        {
            byte[] bytes = File.ReadAllBytes(source);
            using (var input = new MemoryStream(bytes))
            using (var src = new Bitmap(input))
            {
                if (src.Size == size) return bytes;
                using (var bmp = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    double k = Math.Min((double)size.Width / src.Width, (double)size.Height / src.Height);
                    int w = (int)Math.Round(src.Width * k), h = (int)Math.Round(src.Height * k);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new Rectangle((size.Width - w) / 2, (size.Height - h) / 2, w, h));
                    }
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        return ms.ToArray();
                    }
                }
            }
        }

        // Roblox's own cursor over the game world (its backup, if a custom one is in place), as a data URI.
        public static string DefaultImage(string versionDir)
        {
            if (versionDir == null) return null;
            string backup = Path.Combine(versionDir, BackupFolder);
            string file = Path.Combine(backup, Cursors[0]);
            if (!File.Exists(file)) file = Path.Combine(backup, Path.GetFileName(Cursors[0]));   // a 1.0.12/1.0.13 backup
            if (!File.Exists(file)) file = Path.Combine(versionDir, Cursors[0]);
            return File.Exists(file) ? "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(file)) : null;
        }

        // Turns a .png, .cur or .ico file into a 64px PNG with the image centred, as a data URI for the page.
        // Small images keep their own size and stay sharp; bigger ones are scaled down to fit.
        public static string Import(string file)
        {
            using (Bitmap source = Load(file))
            using (var bmp = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                double k = Math.Min(1.0, Math.Min(64.0 / source.Width, 64.0 / source.Height));
                int w = Math.Max(1, (int)Math.Round(source.Width * k)), h = Math.Max(1, (int)Math.Round(source.Height * k));
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = k < 1 ? System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic : System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(source, new Rectangle((64 - w) / 2, (64 - h) / 2, w, h));
                }
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
        }

        // A .cur file is an .ico file with type 2 in its header; Windows' Icon class reads it once that says 1.
        static Bitmap Load(string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".cur" || ext == ".ico")
            {
                byte[] bytes = File.ReadAllBytes(file);
                if (bytes.Length < 6 || bytes[0] != 0 || bytes[1] != 0 || (bytes[2] != 1 && bytes[2] != 2) || bytes[3] != 0)
                    throw new InvalidOperationException("That isn't a cursor or icon file Keystrap can read.");
                bytes[2] = 1;
                using (var ms = new MemoryStream(bytes))
                using (var icon = new Icon(ms, 64, 64))
                    return icon.ToBitmap();
            }
            using (Image img = Image.FromFile(file))
                return new Bitmap(img);
        }
    }

    // Mods: a custom font and custom sounds, put into Keystrap's copy of Roblox on every launch like skies and
    // crosshairs. Roblox's originals are backed up once per version and go back when a mod is reset. A custom font
    // works by pointing every font family (content\fonts\families\*.json) at the one font file. Games that use their
    // own fonts or sounds keep theirs.
    static class Mods
    {
        static readonly string Dir = Path.Combine(Paths.Data, "Mods");
        static readonly string FontDir = Path.Combine(Dir, "Font"), SoundDir = Path.Combine(Dir, "Sounds");
        const string BackupFolder = @"content\keystrap-original-mods";   // mirrors content\
        const string CustomFont = "KeystrapCustomFont";
        static readonly object gate = new object();
        // Every face's font, whether it ships in content\fonts (rbxasset://) or Roblox downloads it (rbxassetid://).
        static readonly Regex AssetId = new Regex(@"""assetId""\s*:\s*""[^""]*""", RegexOptions.Compiled);

        // Sounds that can be replaced: the file in content\sounds, and what it is.
        public static readonly string[][] Sounds =
        {
            new[] { "ouch.ogg", "Death" },
            new[] { "action_jump.mp3", "Jump" },
            new[] { "action_jump_land.mp3", "Landing" },
            new[] { "action_footsteps_plastic.mp3", "Footsteps" },
            new[] { "action_falling.ogg", "Falling" },
            new[] { "action_get_up.mp3", "Getting up" },
            new[] { "action_swim.mp3", "Swimming" },
            new[] { "impact_water.mp3", "Splash" },
            new[] { "impact_explosion_03.mp3", "Explosion" },
            new[] { "volume_slider.ogg", "Volume slider" },
        };

        // Left alone: replacing the fallback for Chinese, Japanese and Korean would turn that text into boxes.
        static readonly string[] KeepFamilies = { "NotoSansCJKFallback" };

        static string FontFile
        {
            get
            {
                if (!Directory.Exists(FontDir)) return null;
                return Directory.GetFiles(FontDir).FirstOrDefault(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase));
            }
        }

        static string Head(string file)
        {
            var b = new byte[4];
            using (var fs = File.OpenRead(file)) fs.Read(b, 0, 4);
            return Encoding.ASCII.GetString(b);
        }

        public static void ImportFont(string file)
        {
            string head = Head(file);
            if (head != "\0\u0001\0\0" && head != "OTTO" && head != "true")
                throw new InvalidOperationException("That isn't a .ttf or .otf font Keystrap can use.");
            lock (gate)
            {
                if (Directory.Exists(FontDir)) Directory.Delete(FontDir, true);
                Directory.CreateDirectory(FontDir);
                File.Copy(file, Path.Combine(FontDir, Path.GetFileName(file)));
            }
        }

        public static void ImportSound(string id, string file)
        {
            if (!Sounds.Any(s => s[0] == id)) throw new InvalidOperationException("Keystrap doesn't replace that sound.");
            if (new FileInfo(file).Length > 5 * 1024 * 1024) throw new InvalidOperationException("Use a sound under 5 MB.");
            byte[] b = File.ReadAllBytes(file);
            string head = Encoding.ASCII.GetString(b, 0, Math.Min(4, b.Length));
            bool audio = head == "OggS" || head.StartsWith("ID3") || head == "RIFF" || (b.Length > 1 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0);
            if (!audio) throw new InvalidOperationException("Use an .ogg, .mp3 or .wav sound.");
            lock (gate)
            {
                Directory.CreateDirectory(SoundDir);
                File.WriteAllBytes(Path.Combine(SoundDir, id), b);
                File.WriteAllText(Path.Combine(SoundDir, id + ".name"), Path.GetFileName(file));
            }
        }

        // kind "font", or "sound" with the sound's file name as id.
        public static void Reset(string kind, string id)
        {
            lock (gate)
            {
                if (kind == "font") { if (Directory.Exists(FontDir)) Directory.Delete(FontDir, true); return; }
                if (!Sounds.Any(s => s[0] == id)) return;
                foreach (string f in new[] { Path.Combine(SoundDir, id), Path.Combine(SoundDir, id + ".name") })
                    if (File.Exists(f)) File.Delete(f);
            }
        }

        // Puts the saved mods into a Roblox version folder, and Roblox's own files back where a mod was reset.
        public static void ApplyTo(string versionDir)
        {
            lock (gate)
            {
                string content = Path.Combine(versionDir, "content"), backup = Path.Combine(versionDir, BackupFolder);
                int replaced = 0;
                foreach (string[] s in Sounds)
                {
                    string custom = Path.Combine(SoundDir, s[0]);
                    replaced += Place(content, backup, Path.Combine("sounds", s[0]), File.Exists(custom) ? File.ReadAllBytes(custom) : null);
                }

                string fonts = Path.Combine(content, "fonts"), font = FontFile, asset = null;
                if (!Directory.Exists(fonts)) return;
                foreach (string old in Directory.GetFiles(fonts, CustomFont + ".*")) File.Delete(old);
                if (font != null)
                {
                    string name = CustomFont + Path.GetExtension(font).ToLowerInvariant();
                    File.Copy(font, Path.Combine(fonts, name));
                    asset = "\"assetId\": \"rbxasset://fonts/" + name + "\"";
                }
                string families = Path.Combine(fonts, "families");
                if (Directory.Exists(families))
                    foreach (string json in Directory.GetFiles(families, "*.json"))
                    {
                        if (KeepFamilies.Contains(Path.GetFileNameWithoutExtension(json))) continue;
                        string rel = Path.Combine("fonts", "families", Path.GetFileName(json));
                        byte[] updated = null;
                        if (asset != null)
                        {
                            string original = Path.Combine(backup, rel);
                            string text = File.ReadAllText(File.Exists(original) ? original : json);
                            updated = Encoding.UTF8.GetBytes(AssetId.Replace(text, asset));
                        }
                        replaced += Place(content, backup, rel, updated);
                    }
                if (replaced > 0) Log.Write("Mods: " + replaced + " Roblox files set in " + versionDir + (font != null ? " (font " + Path.GetFileName(font) + ")" : ""));
            }
        }

        // Writes bytes over content\rel after backing up Roblox's file; null puts Roblox's file back, if it was replaced.
        static int Place(string content, string backup, string rel, byte[] bytes)
        {
            string target = Path.Combine(content, rel), original = Path.Combine(backup, rel);
            if (!File.Exists(target)) return 0;
            if (bytes == null)
            {
                if (!File.Exists(original)) return 0;
                File.Copy(original, target, true);
                File.Delete(original);
                return 1;
            }
            if (!File.Exists(original))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(original));
                File.Copy(target, original);
            }
            File.WriteAllBytes(target, bytes);
            return 1;
        }

        // What the Mods page shows: the font (with its data for a preview) and each sound, playable.
        public static Dictionary<string, object> Describe(string versionDir)
        {
            string font = FontFile;
            var sounds = Sounds.Select(s =>
            {
                string custom = Path.Combine(SoundDir, s[0]), nameFile = custom + ".name";
                string source = File.Exists(custom) ? custom : RobloxFile(versionDir, Path.Combine("sounds", s[0]));
                return new Dictionary<string, object>
                {
                    { "id", s[0] },
                    { "name", s[1] },
                    { "custom", File.Exists(custom) ? (File.Exists(nameFile) ? File.ReadAllText(nameFile) : s[0]) : null },
                    { "preview", source != null ? "data:" + AudioMime(source) + ";base64," + Convert.ToBase64String(File.ReadAllBytes(source)) : null },
                };
            }).ToList();
            return new Dictionary<string, object>
            {
                { "type", "mods" },
                { "font", font == null ? null : new Dictionary<string, object> {
                    { "name", Path.GetFileName(font) },
                    { "data", new FileInfo(font).Length < 8 * 1024 * 1024 ? "data:font/" + Path.GetExtension(font).TrimStart('.') + ";base64," + Convert.ToBase64String(File.ReadAllBytes(font)) : null } } },
                { "sounds", sounds },
            };
        }

        // The audio type from the file's own first bytes, not the Roblox slot's extension, so a .wav (or an .mp3 put in
        // an .ogg slot, and the other way round) previews with the right type instead of a guessed one.
        static string AudioMime(string file)
        {
            try
            {
                var b = new byte[4];
                using (var fs = File.OpenRead(file))
                    if (fs.Read(b, 0, 4) >= 2)
                    {
                        if (b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S') return "audio/ogg";
                        if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F') return "audio/wav";
                        if ((b[0] == 'I' && b[1] == 'D' && b[2] == '3') || (b[0] == 0xFF && (b[1] & 0xE0) == 0xE0)) return "audio/mpeg";
                    }
            }
            catch { }
            return "audio/mpeg";
        }

        // Roblox's own copy of a content file: its backup if a mod replaced it, otherwise the file itself.
        static string RobloxFile(string versionDir, string rel)
        {
            if (versionDir == null) return null;
            string original = Path.Combine(versionDir, BackupFolder, rel), current = Path.Combine(versionDir, "content", rel);
            return File.Exists(original) ? original : File.Exists(current) ? current : null;
        }
    }

    // Roblox's public web APIs answer 429 (Too Many Requests) when a PC asks too often. Every Keystrap lookup goes through
    // RobloxApi: a 429 is retried after the wait Roblox asks for (Retry-After), or after 1, 2 and 4 seconds when it doesn't
    // say, and a host that's still limiting is left alone until its wait is over, so Keystrap never adds to the block.
    class RateLimitedException : Exception
    {
        public readonly TimeSpan Wait;

        public RateLimitedException(string host, TimeSpan wait)
            : base("Roblox is getting too many requests from this PC. Wait " + RobloxApi.Describe(wait) + ", then try again.")
        {
            Wait = wait;
            Data["host"] = host;
        }
    }

    static class RobloxApi
    {
        const int Attempts = 4;                                          // the first try and three retries
        static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);     // a longer wait isn't sat through; the caller is told
        static readonly Dictionary<string, DateTime> blockedUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public static async Task<string> GetStringAsync(HttpClient http, string url)
        {
            string host = new Uri(url).Host;
            TimeSpan left = BlockedFor(host);
            if (left > TimeSpan.Zero) throw new RateLimitedException(host, left);

            for (int attempt = 1; ; attempt++)
            {
                TimeSpan wait;
                using (HttpResponseMessage resp = await http.GetAsync(url))
                {
                    if ((int)resp.StatusCode != 429)
                    {
                        resp.EnsureSuccessStatusCode();
                        return await resp.Content.ReadAsStringAsync();
                    }
                    wait = RetryAfter(resp) ?? TimeSpan.FromSeconds(1 << (attempt - 1));
                }
                lock (blockedUntil) blockedUntil[host] = DateTime.UtcNow + wait;
                bool retry = attempt < Attempts && wait <= MaxWait;
                Log.Write(host + " is rate limiting Keystrap (429)" + (retry ? "; retrying in " + Describe(wait) : "; giving up for " + Describe(wait)));
                if (!retry) throw new RateLimitedException(host, wait);
                await Task.Delay(wait);
            }
        }

        // "45 seconds", "3 minutes": rounded up, so the wait shown is never too short.
        public static string Describe(TimeSpan wait)
        {
            int s = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            if (s < 120) return s + (s == 1 ? " second" : " seconds");
            return (int)Math.Ceiling(s / 60.0) + " minutes";
        }

        static TimeSpan BlockedFor(string host)
        {
            DateTime until;
            lock (blockedUntil)
                return blockedUntil.TryGetValue(host, out until) && until > DateTime.UtcNow ? until - DateTime.UtcNow : TimeSpan.Zero;
        }

        // Retry-After is either a number of seconds or a date.
        static TimeSpan? RetryAfter(HttpResponseMessage resp)
        {
            var header = resp.Headers.RetryAfter;
            if (header == null) return null;
            if (header.Delta.HasValue) return header.Delta.Value;
            if (header.Date.HasValue)
            {
                TimeSpan until = header.Date.Value - DateTimeOffset.UtcNow;
                return until > TimeSpan.Zero ? until : TimeSpan.Zero;
            }
            return null;
        }
    }

    // Creator name and icon for an experience, from Roblox's public APIs, cached per universe.
    static class RobloxGames
    {
        public class Info { public string Creator; public string Icon; }
        static readonly Dictionary<long, Info> cache = new Dictionary<long, Info>();     // only complete results (icon found)
        static readonly Dictionary<long, DateTime> retryAfter = new Dictionary<long, DateTime>();   // failed lookups, when to try again
        static readonly HashSet<long> loading = new HashSet<long>();
        static readonly HttpClient http = CreateClient();
        static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(60);

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
            DateTime until;
            if (retryAfter.TryGetValue(universe, out until) && DateTime.UtcNow < until) return null;   // a recent lookup failed; wait
            if (loading.Add(universe)) Load(universe, loaded);
            return null;
        }

        static async void Load(long universe, Action loaded)
        {
            var info = new Info();
            var json = new JavaScriptSerializer();
            try
            {
                var g = json.Deserialize<Dictionary<string, object>>(await RobloxApi.GetStringAsync(http, "https://games.roblox.com/v1/games?universeIds=" + universe));
                var game = g.ContainsKey("data") && g["data"] is ArrayList && ((ArrayList)g["data"]).Count > 0 ? ((ArrayList)g["data"])[0] as Dictionary<string, object> : null;
                var creator = game != null && game.ContainsKey("creator") ? game["creator"] as Dictionary<string, object> : null;
                if (creator != null && creator.ContainsKey("name")) info.Creator = creator["name"] as string;
            }
            catch (Exception ex) { Log.Write("Couldn't look up the creator of universe " + universe + ": " + ex.Message); }
            try
            {
                var t = json.Deserialize<Dictionary<string, object>>(await RobloxApi.GetStringAsync(http,
                    "https://thumbnails.roblox.com/v1/games/icons?universeIds=" + universe + "&size=512x512&format=Png&isCircular=false"));
                var img = t.ContainsKey("data") && t["data"] is ArrayList && ((ArrayList)t["data"]).Count > 0 ? ((ArrayList)t["data"])[0] as Dictionary<string, object> : null;
                if (img != null && img.ContainsKey("imageUrl")) info.Icon = img["imageUrl"] as string;
            }
            catch (Exception ex) { Log.Write("Couldn't look up the icon of universe " + universe + ": " + ex.Message); }
            loading.Remove(universe);
            if (info.Icon != null) { cache[universe] = info; retryAfter.Remove(universe); }   // keep a good result for the session
            else retryAfter[universe] = DateTime.UtcNow + RetryDelay;   // failed or still pending: let it be looked up again later
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
                string json = await RobloxApi.GetStringAsync(http, "https://clientsettings.roblox.com/v2/client-version/WindowsPlayer");
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
            string guid = (await RobloxApi.GetStringAsync(http, Cdn + "versionGuid")).Trim();
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

        // Older versions of Keystrap wrote flags here, and Roblox would keep reading them until the file is gone.
        public static void RemoveOldClientSettings(string dir)
        {
            string file = Path.Combine(dir, "ClientSettings", "ClientAppSettings.json");
            if (!File.Exists(file)) return;
            try
            {
                File.Delete(file);
                Log.Write("Removed the old ClientAppSettings.json");
            }
            catch (Exception ex) { Log.Write("Couldn't remove the old ClientAppSettings.json: " + ex.Message); }
        }

        // Roblox's in-game Maximum Frame Rate, kept in its own settings file. -1 ("not set") leaves Roblox on its 60 FPS
        // default. Never throws; Roblox may be rewriting the file.
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
            public string Server;   // the Roblox server's public address, once the log names it
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
        static readonly TimeSpan StaleSession = TimeSpan.FromMinutes(3);   // an open session whose log went this quiet is over
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static readonly Regex Stamp = new Regex(@"^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z)", RegexOptions.Compiled);
        static readonly Regex JoinLine = new Regex(@"\[FLog::Output\] ! Joining game '([0-9a-fA-F\-]+)' place (\d+)", RegexOptions.Compiled);
        static readonly Regex UniverseLine = new Regex(@"\[FLog::GameJoinLoadTime\] Report game_join_loadtime: placeid:(\d+).*?universeid:(\d+)", RegexOptions.Compiled);
        static readonly Regex ServerLine = new Regex(@"\[FLog::Network\] UDMUX Address = (\d{1,3}(?:\.\d{1,3}){3})", RegexOptions.Compiled);
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
                    // An instance that crashed or was force-closed stops writing to its log without a leave line. If its
                    // session is still open but the log has gone quiet, end it — so one dead window doesn't stay in
                    // "Playing now" while other multi-instance windows keep Roblox running.
                    if (st.Open != null && fi.LastWriteTimeUtc < DateTime.UtcNow - StaleSession)
                        any |= Close(st, st.LastStamp);
                }
            }

            // Roblox fully closed (or crashed): end any session still open, without waiting out the stale timeout.
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
                m = ServerLine.Match(line);
                if (m.Success && st.Open != null)
                {
                    st.Open.Server = m.Groups[1].Value;
                    ResolveRegion(st.Open.Server);
                    any = true;
                    continue;
                }
                if (st.Open != null && LeaveLine.IsMatch(line)) any |= Close(st, when);
            }
            return any;
        }

        // Where a Roblox server is ("Ashburn, Virginia, US"), looked up once per address from ipinfo.io. Only the
        // server's address is sent, never anything about the player. Roblox's servers don't answer pings, so live
        // ping is left to Roblox's own Shift+F3 stats.
        readonly Dictionary<string, string> regions = new Dictionary<string, string>();
        readonly HashSet<string> resolvingRegions = new HashSet<string>();

        async void ResolveRegion(string ip)
        {
            if (regions.ContainsKey(ip) || !resolvingRegions.Add(ip)) return;
            try
            {
                var d = json.Deserialize<Dictionary<string, object>>(await RobloxApi.GetStringAsync(http, "https://ipinfo.io/" + ip + "/json"));
                var parts = new[] { "city", "region", "country" }
                    .Select(k => d.ContainsKey(k) ? d[k] as string : null)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct();
                string place = string.Join(", ", parts);
                if (place.Length == 0) return;
                regions[ip] = place;
                Log.Write("Roblox server " + ip + " is in " + place);
                changed();
            }
            catch (Exception ex) { Log.Write("Couldn't look up where Roblox server " + ip + " is: " + ex.Message); }
            finally { resolvingRegions.Remove(ip); }
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
                        await RobloxApi.GetStringAsync(http, "https://apis.roblox.com/universes/v1/places/" + place + "/universe"));
                    object id;
                    if (!u.TryGetValue("universeId", out id) || id == null) return;
                    universe = Convert.ToInt64(id, CultureInfo.InvariantCulture);
                    placeUniverse[place] = universe;
                }
                if (!titles.ContainsKey("u" + universe))
                {
                    var g = json.Deserialize<Dictionary<string, object>>(
                        await RobloxApi.GetStringAsync(http, "https://games.roblox.com/v1/games?universeIds=" + universe));
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

        // The server being played now, or the last one that was played — place and job id, for a best-effort rejoin.
        public class ServerRef { public long Place; public string JobId; public string Title; }
        public ServerRef LastServer()
        {
            Session s = logs.Values.Where(x => x.Open != null).Select(x => x.Open).OrderByDescending(x => x.Start).FirstOrDefault()
                      ?? sessions.OrderByDescending(x => x.End).FirstOrDefault();
            if (s == null || string.IsNullOrEmpty(s.JobId) || s.Place <= 0) return null;
            return new ServerRef { Place = s.Place, JobId = s.JobId, Title = TitleOf(s) };
        }

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
                { "server", st.Open.Server },
                { "region", st.Open.Server != null && regions.ContainsKey(st.Open.Server) ? regions[st.Open.Server] : null },
            }).ToList();
            ServerRef recent = LastServer();
            return new Dictionary<string, object>
            {
                { "type", "playtime" },
                { "games", games },
                { "current", current },
                { "totalSeconds", all.Sum(d => (long)d["seconds"]) },
                { "gameCount", all.Count },
                { "sessionCount", all.Sum(d => (int)d["sessions"]) },
                { "lastServer", recent == null ? null : new Dictionary<string, object> { { "placeId", recent.Place }, { "title", recent.Title } } },
            };
        }

        void Load()
        {
            try
            {
                if (!File.Exists(StoreFile)) { Log.Write("Playtime: no history file yet at " + StoreFile); return; }
                ReadFromDisk();
                Log.Write("Playtime: loaded " + sessions.Count + " sessions, " + titles.Count + " titles");
            }
            catch (Exception ex)
            {
                Log.Write("playtime.json couldn't be read, starting fresh: " + ex.Message);
                try { File.Copy(StoreFile, StoreFile + ".bad", true); } catch { }
            }
        }

        // Reads titles, places and sessions from the store file, adding only sessions we don't already hold
        // (deduped by job + start, the same key Close uses). Safe to call repeatedly. Save calls it right before
        // writing, so anything another Keystrap instance saved is absorbed first and a save can only ever grow the
        // history — it can't shrink it or wipe it, even if this instance somehow loaded an incomplete view.
        void ReadFromDisk()
        {
            if (!File.Exists(StoreFile)) return;
            var d = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(StoreFile));
            object o;
            if (d.TryGetValue("titles", out o) && o is Dictionary<string, object>)
                foreach (var kv in (Dictionary<string, object>)o) if (kv.Value is string) titles[kv.Key] = (string)kv.Value;
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
                    if (recorded.Add(s.JobId + "|" + s.Start.Ticks)) sessions.Add(s);   // skip ones we already have
                }
        }

        void Save()
        {
            try
            {
                // Pull in anything on disk we don't already have (another instance, or history this run didn't load)
                // before overwriting, so a save can only grow the file. If the file is unreadable right now, skip the
                // merge and write what we have — never block saving on it.
                try { ReadFromDisk(); } catch (Exception ex) { Log.Write("Playtime: couldn't merge on save: " + ex.Message); }
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

    // Asks GitHub whether a newer Keystrap is out. It uses the same rules as the updater: a version tag, not a draft or
    // pre-release, with Keystrap.zip or Keystrap.exe attached. The updater does the actual download and install.
    static class UpdateCheck
    {
        const string ReleasesUrl = "https://api.github.com/repos/deme06357-eng/Keystrap/releases?per_page=30";
        static readonly Regex VersionTag = new Regex(@"^[vV]?\d+(\.\d+)+$");

        // The newest release's tag if it's newer than this copy, otherwise null. feedFile: a saved copy of GitHub's answer, for testing.
        public static async Task<string> NewerVersionAsync(string feedFile)
        {
            string text = feedFile != null ? File.ReadAllText(feedFile) : await DownloadReleasesAsync();
            var releases = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text) as object[];
            if (releases == null) return null;

            string newest = null;
            Version best = Parse(AppInfo.Version);
            foreach (var release in releases.OfType<Dictionary<string, object>>().Where(IsInstallable))
            {
                string tag = ((string)release["tag_name"]).Trim();
                Version version = Parse(tag);
                if (version > best) { best = version; newest = tag; }
            }
            return newest;
        }

        static async Task<string> DownloadReleasesAsync()
        {
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(10);
                http.DefaultRequestHeaders.UserAgent.ParseAdd(WebhookService.UserAgent());
                return await http.GetStringAsync(ReleasesUrl);
            }
        }

        static bool IsInstallable(Dictionary<string, object> release)
        {
            string tag = release.ContainsKey("tag_name") ? release["tag_name"] as string : null;
            if (tag == null || !VersionTag.IsMatch(tag.Trim())) return false;
            if (true.Equals(release.ContainsKey("draft") ? release["draft"] : null)) return false;
            if (true.Equals(release.ContainsKey("prerelease") ? release["prerelease"] : null)) return false;
            var assets = release.ContainsKey("assets") ? release["assets"] as object[] : null;
            return assets != null && assets.OfType<Dictionary<string, object>>().Any(a =>
            {
                string name = a.ContainsKey("name") ? a["name"] as string : null;
                return string.Equals(name, "Keystrap.zip", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Keystrap.exe", StringComparison.OrdinalIgnoreCase);
            });
        }

        static Version Parse(string tag) { return new Version(tag.Trim().TrimStart('v', 'V')); }
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
            // Links never open on their own: the page asks first ("You're leaving Keystrap") and then sends
            // "open-url". A link that tries to open a window, or to take this one somewhere else, is sent back to
            // the page to ask about.
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                Post(new Dictionary<string, object> { { "type", "confirm-url" }, { "url", e.Uri } });
            };
            core.NavigationStarting += (s, e) =>
            {
                if (e.Uri.StartsWith("https://keystrap.app/", StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                Post(new Dictionary<string, object> { { "type", "confirm-url" }, { "url", e.Uri } });
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
            // WebView2 only works on the UI thread. From any other thread the read below throws and the message would be lost.
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)(() => Post(message))); } catch (InvalidOperationException) { }
                return;
            }

            // Reading CoreWebView2 can throw — the control is already gone while the window is closing, or the OS doesn't
            // really support WebView2 (its COM controller doesn't offer the interface, as on Windows 8). Either way there
            // is nothing to deliver to, so drop the message instead of letting a background Post crash Keystrap.
            try
            {
                var core = web.CoreWebView2;
                if (core != null) core.PostWebMessageAsJson(json.Serialize(message));
            }
            catch (Exception) { }
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

        const int UncappedFps = 9999;
        const int MenuFps = 60;
        const int DefaultFps = 60;   // where Roblox's Maximum Frame Rate goes back to when Unlock FPS is turned off

        // The frame rate this launch starts Roblox at, or 0 to leave Roblox's own setting alone. Roblox reads it once at
        // startup, so it holds for the whole session: joining a game from inside the app home stays at the menu cap.
        // toAppHome: no game link, so Roblox opens on its home screen.
        static int LaunchFps(Dictionary<string, object> msg, bool toAppHome)
        {
            if (toAppHome && Bool(msg, "menuCap")) { int m = Int(msg, "menuFps"); return m >= 10 && m <= 240 ? m : MenuFps; }
            return Bool(msg, "unlockFps") ? UncappedFps : 0;
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
                    if (Program.DevMode)
                        SendTestNotificationsAsync(msg.ContainsKey("target") ? msg["target"] as string : "crash", Int(msg, "count"));
                    break;
                case "update-now":
                    StartUpdater();
                    break;
                case "post-faq":
                    if (Program.DevMode)
                        PostFaqAsync(msg.ContainsKey("items") ? msg["items"] : null);
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
                case "open-url":
                    OpenWebsite(msg.ContainsKey("url") ? msg["url"] as string : null);
                    break;
                case "rejoin":
                    RejoinLastServer();
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
                case "crosshair-save":
                    SaveCrosshairAsync(msg.ContainsKey("far") ? msg["far"] as string : null, msg.ContainsKey("locked") ? msg["locked"] as string : null);
                    break;
                case "crosshair-import":
                    ImportCrosshair();
                    break;
                case "mods":
                    SendModsAsync();
                    break;
                case "mod-import":
                    ImportMod(msg.ContainsKey("kind") ? msg["kind"] as string : null, msg.ContainsKey("id") ? msg["id"] as string : null);
                    break;
                case "mod-reset":
                    try { Mods.Reset(msg["kind"] as string, msg.ContainsKey("id") ? msg["id"] as string : null); }
                    catch (Exception ex) { Log.Write("Resetting a mod failed: " + ex.Message); }
                    SendModsAsync();
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
                var items = await Task.Run(() => { Skies.RemoveRetired(); return Skies.List(dir); });
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

        // ---------- Custom Crosshairs ----------

        // Roblox's own crosshair, for the page's "Roblox Default" card and preview.
        async void SendCrosshairDefaultAsync()
        {
            try
            {
                string dir = InstalledDir();
                string image = await Task.Run(() => Crosshairs.DefaultImage(dir));
                Post(new Dictionary<string, object> { { "type", "crosshair-default" }, { "image", image } });
            }
            catch (Exception ex) { Log.Write("Reading Roblox's crosshair failed: " + ex.Message); }
        }

        // The page sends the crosshair drawn at 64px and 32px (or neither, for Roblox's own); it's kept and put into
        // the installed Roblox straight away.
        async void SaveCrosshairAsync(string far, string locked)
        {
            try
            {
                string dir = InstalledDir();
                await Task.Run(() =>
                {
                    Crosshairs.Save(far, locked);
                    if (dir != null) Crosshairs.ApplyTo(dir);
                });
                Process[] running = Process.GetProcessesByName("RobloxPlayerBeta");
                bool robloxOpen = running.Length > 0;
                foreach (Process p in running) p.Dispose();
                Post(new Dictionary<string, object> { { "type", "crosshair-saved" }, { "ok", true }, { "robloxOpen", robloxOpen } });
            }
            catch (Exception ex)
            {
                Log.Write("Saving the crosshair failed: " + ex);
                string message = ex is IOException || ex is UnauthorizedAccessException ? "Roblox is using its cursor files. Close Roblox, then try again."
                    : "Keystrap couldn't save that crosshair. Details are in the log.";
                Post(new Dictionary<string, object> { { "type", "crosshair-saved" }, { "ok", false }, { "message", message } });
            }
        }

        void ImportCrosshair()
        {
            string file;
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Choose a crosshair image";
                dlg.Filter = "Crosshair images|*.png;*.cur;*.ico|All files|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                file = dlg.FileName;
            }
            ImportCrosshairAsync(file);
        }

        async void ImportCrosshairAsync(string file)
        {
            try
            {
                string image = await Task.Run(() => Crosshairs.Import(file));
                Post(new Dictionary<string, object> { { "type", "crosshair-imported" }, { "image", image } });
            }
            catch (Exception ex)
            {
                Log.Write("Importing a crosshair failed: " + ex.Message);
                string message = ex is InvalidOperationException ? ex.Message : "Keystrap couldn't read that file. Use a .png, .cur or .ico image.";
                Post(new Dictionary<string, object> { { "type", "crosshair-import-error" }, { "message", message } });
            }
        }

        // ---------- Outside links ----------

        // Opens a website in the user's browser, after they said Continue on the page's warning. Only real web
        // addresses: anything else (a file, a program, another kind of link) is refused.
        // Best-effort rejoin of the last server. Joining a specific instance needs an auth ticket only Roblox can mint,
        // and Keystrap never touches your login, so this opens the Roblox join page in your browser: it signs you in and
        // hands the launch back to Keystrap (when Keystrap handles Roblox links). Roblox may still drop you in another
        // server — it sometimes ignores gameInstanceId — and a server that has shut down can't be rejoined.
        void RejoinLastServer()
        {
            PlaytimeTracker.ServerRef s = playtime != null ? playtime.LastServer() : null;
            if (s == null) { Toast("No Roblox server to rejoin yet — play one first."); return; }
            OpenWebsite("https://www.roblox.com/games/start?placeId=" + s.Place + "&gameInstanceId=" + Uri.EscapeDataString(s.JobId));
            Toast("Opening Roblox in your browser to rejoin your last server…");
        }

        void OpenWebsite(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                Log.Write("Refused to open a link that isn't a web address: " + url);
                return;
            }
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("Opening " + uri.Host + " failed: " + ex.Message); Toast("Keystrap couldn't open your browser."); }
        }

        // ---------- Multi-instance ----------

        // Roblox opens only one window while it holds ROBLOX_singletonMutex. Keystrap takes it first, so every Roblox
        // that starts finds it taken and runs anyway. If a Roblox started without Keystrap already holds it, Keystrap
        // can't take it until that Roblox closes: false, and the next launch tries again.
        bool HoldRobloxSingleton()
        {
            if (robloxSingleton != null) return true;
            bool created;
            var mutex = new Mutex(true, "ROBLOX_singletonMutex", out created);
            if (!created)
            {
                bool owned;
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }   // its owner closed without letting go: ours now
                if (!owned)
                {
                    mutex.Dispose();
                    Log.Write("Multi-instance: Roblox already holds its window lock; it works after every Roblox window closes");
                    return false;
                }
            }
            robloxSingleton = mutex;
            Log.Write("Multi-instance: holding Roblox's window lock");
            return true;
        }

        // ---------- Mods ----------

        async void SendModsAsync()
        {
            try
            {
                string dir = InstalledDir();
                Post(await Task.Run(() => Mods.Describe(dir)));
            }
            catch (Exception ex) { Log.Write("Listing mods failed: " + ex.Message); }
        }

        // kind "font", or "sound" with the sound's file name as id. Saved now, put into Roblox on the next launch.
        void ImportMod(string kind, string id)
        {
            string file;
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = kind == "font" ? "Choose a font" : "Choose a sound";
                dlg.Filter = kind == "font" ? "Fonts|*.ttf;*.otf" : "Sounds|*.ogg;*.mp3;*.wav";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                file = dlg.FileName;
            }
            ImportModAsync(kind, id, file);
        }

        async void ImportModAsync(string kind, string id, string file)
        {
            try
            {
                await Task.Run(() => { if (kind == "font") Mods.ImportFont(file); else Mods.ImportSound(id, file); });
                Post(new Dictionary<string, object> { { "type", "mod-saved" } });
                SendModsAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Importing a mod failed: " + ex.Message);
                string message = ex is InvalidOperationException ? ex.Message : "Keystrap couldn't read that file. Details are in the log.";
                Post(new Dictionary<string, object> { { "type", "mod-error" }, { "message", message } });
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
            bool openedByLink = pendingLink != null;
            if (pendingLink != null) { Post(new Dictionary<string, object> { { "type", "launch-link" }, { "link", pendingLink } }); pendingLink = null; }
            if (!openedByLink) CheckForUpdateAsync();   // someone joining a game shouldn't get interrupted
            if (playtimeReady) Post(playtime.Snapshot());
            SendSkiesAsync();
            SendCrosshairDefaultAsync();
            SendModsAsync();
            SendVersionsSizeAsync();
            CheckRobloxAsync();
        }

        // Tells the page when a newer release is out. A dev build skips this, so it never offers to replace itself with
        // a public one. A failed check only gets logged; it can try again next time Keystrap opens.
        async void CheckForUpdateAsync()
        {
            if (Program.DevMode) return;
            try
            {
                string version = await UpdateCheck.NewerVersionAsync(Program.UpdateFeed);
                Log.Write(version == null ? "Keystrap is up to date" : "Update available: " + version);
                if (version != null) Post(new Dictionary<string, object> { { "type", "update-available" }, { "version", version } });
            }
            catch (Exception ex) { Log.Write("Update check failed: " + ex.Message); }
        }

        // The updater closes Keystrap itself, installs the release and opens Keystrap again.
        void StartUpdater()
        {
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                Process.Start(new ProcessStartInfo(Path.Combine(dir, "Keystrap.Updater.exe")) { UseShellExecute = true, WorkingDirectory = dir });
            }
            catch (Exception ex)
            {
                Log.Write("Couldn't start the updater: " + ex.Message);
                Toast("Couldn't start the updater.");
            }
        }

        // Developer mode: 1 to 3 test messages to one channel, in that channel's real format, spaced out so
        // Discord's webhook rate limit (5 per 2 s) isn't hit. target: "crash" or "commits".
        // Posts the About page's FAQ to the FAQ webhook (Dev/Testing only). The id of the message it posts is kept in
        // faq-message.txt, so the next press edits that same message ("refresh") instead of posting a new one; if the
        // message was deleted (404), it posts a fresh one. The page always gets a result, which is what resets its button.
        async void PostFaqAsync(object rawItems)
        {
            Dictionary<string, object> result = null;
            try
            {
                result = await SendFaqAsync(rawItems);
            }
            catch (Exception ex)
            {
                Log.Write("Posting the FAQ failed: " + ex.Message);
                result = FaqResult(false, "Couldn't reach Discord. Check your internet connection and try again.");
            }
            finally
            {
                Post(result ?? FaqResult(false, "Couldn't reach Discord. Check your internet connection and try again."));
            }
        }

        async Task<Dictionary<string, object>> SendFaqAsync(object rawItems)
        {
            if (Secrets.FaqWebhook.Length == 0) return FaqResult(false, "No FAQ webhook is set up (secrets.json).");

            var fields = new List<object>();
            var items = rawItems as ArrayList;
            if (items != null)
                foreach (object it in items)
                {
                    var d = it as Dictionary<string, object>;
                    if (d == null) continue;
                    string q = (d.ContainsKey("q") ? d["q"] as string : null) ?? "";
                    string a = (d.ContainsKey("a") ? d["a"] as string : null) ?? "";
                    if (q.Length == 0) continue;
                    fields.Add(new Dictionary<string, object> { { "name", WebhookService.Clip(q, 256) }, { "value", WebhookService.Clip(a, 1024) }, { "inline", false } });
                }
            if (fields.Count == 0) return FaqResult(false, "There are no FAQ entries to post.");

            var embed = new Dictionary<string, object>
            {
                { "title", "Keystrap — FAQ" },
                { "color", 0xFDE68A },
                { "fields", fields.ToArray() },
                { "footer", new Dictionary<string, object> { { "text", "Keystrap FAQ · updated " + DateTime.UtcNow.ToString("MM/dd/yy, hh:mm tt", CultureInfo.InvariantCulture) + " UTC" } } },
            };
            var payload = new Dictionary<string, object>
            {
                { "username", "Keystrap" },
                { "allowed_mentions", new Dictionary<string, object> { { "parse", new string[0] } } },
                { "embeds", new object[] { embed } },
            };
            string body = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(payload);

            string idFile = Path.Combine(Paths.Data, "faq-message.txt");
            string existingId = null;
            try { if (File.Exists(idFile)) existingId = File.ReadAllText(idFile).Trim(); } catch (Exception) { }

            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(15);
                http.DefaultRequestHeaders.UserAgent.ParseAdd(WebhookService.UserAgent());

                if (!string.IsNullOrEmpty(existingId))
                {
                    var patch = new HttpRequestMessage(new HttpMethod("PATCH"), Secrets.FaqWebhook + "/messages/" + existingId)
                        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    using (HttpResponseMessage resp = await http.SendAsync(patch))
                    {
                        if (resp.IsSuccessStatusCode) return FaqResult(true, "Refreshed the FAQ message in Discord.");
                        if ((int)resp.StatusCode != 404) return FaqResult(false, "Discord didn't accept the update (HTTP " + (int)resp.StatusCode + ").");
                        // 404: the old message is gone, so post a fresh one below.
                    }
                }

                using (HttpResponseMessage resp = await http.PostAsync(Secrets.FaqWebhook + "?wait=true",
                    new StringContent(body, Encoding.UTF8, "application/json")))
                {
                    if (!resp.IsSuccessStatusCode) return FaqResult(false, "Discord didn't accept the FAQ (HTTP " + (int)resp.StatusCode + ").");
                    string json = await resp.Content.ReadAsStringAsync();
                    try
                    {
                        var sent = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                        object id;
                        if (sent != null && sent.TryGetValue("id", out id) && id != null)
                            File.WriteAllText(idFile, Convert.ToString(id, CultureInfo.InvariantCulture));
                    }
                    catch (Exception) { }
                    return FaqResult(true, "Posted the FAQ to Discord.");
                }
            }
        }

        static Dictionary<string, object> FaqResult(bool ok, string message)
        {
            return new Dictionary<string, object> { { "type", "faq-posted" }, { "ok", ok }, { "message", message } };
        }

        async void SendTestNotificationsAsync(string target, int count)
        {
            count = Math.Max(1, Math.Min(3, count));
            int sent = 0;
            string error = null;
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    if (!await WebhookService.SendAsync(TestNotification(target, i))) throw new InvalidOperationException(WebhookService.LastError);
                    sent++;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Log.Write("Test notification " + i + " to " + target + " failed: " + ex.Message);
                }
                if (i < count) await Task.Delay(700);
            }
            Log.Write("Test notifications to " + target + ": " + sent + " of " + count + " sent");
            Post(new Dictionary<string, object>
            {
                { "type", "webhook-test-done" }, { "target", target }, { "sent", sent }, { "total", count }, { "error", error },
                { "crashChannel", WebhookService.HasCrashChannel },
            });
        }

        // Test messages look like the real ones: crashes alternate between kept-running and closed, commits
        // read like the changelog hook's posts.
        static Notification TestNotification(string target, int number)
        {
            if (target == "commits")
                return new Notification(NotificationKind.Commits, "Keystrap " + AppInfo.Version.TrimStart('v') + " (test)", "1 new commit, 1 file changed")
                    .Field("Test commit " + number, "Developer mode test from Settings → Test notifications.");
            Exception sample;
            try { throw new InvalidOperationException("Test crash " + number + " from Settings → Test notifications."); }
            catch (Exception ex) { sample = ex; }
            return CrashReporter.BuildNotification(sample, "Test notification", number % 2 == 0);
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

        // One launch, start to finish: settings that must be written before Roblox starts, the right Roblox version,
        // the user's customisations on top of it, then Roblox itself.
        async void LaunchAsync(Dictionary<string, object> msg)
        {
            if (launching) return;
            launching = true;
            try
            {
                string link = msg.ContainsKey("link") ? msg["link"] as string : null;
                bool toAppHome = !RobloxLink.IsLink(link);
                ApplyFrameRate(msg, toAppHome);

                RobloxVersion version = await PickVersionAsync();
                string dir = await Task.Run(() => Bootstrapper.InstallAsync(version, ReportProgress));
                await ApplyCustomisationsAsync(dir, msg);
                Bootstrapper.RemoveOldClientSettings(dir);

                if (Bool(msg, "multiInstance") && !HoldRobloxSingleton())
                    Toast("Multi-instance starts working once every Roblox window is closed and you launch again from Keystrap.");

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
                var error = new Dictionary<string, object> { { "type", "launch-error" }, { "message", LaunchErrorText(ex) } };
                // The page locks the Launch button for the wait, so retrying can't make the block longer.
                var limited = ex as RateLimitedException;
                if (limited != null) error["retryAfter"] = (int)Math.Ceiling(limited.Wait.TotalSeconds);
                Post(error);
            }
            finally
            {
                launching = false;
            }
        }

        // The frame rate goes into Roblox's Maximum Frame Rate, written before Roblox starts, since that's the only
        // time it reads it.
        void ApplyFrameRate(Dictionary<string, object> msg, bool toAppHome)
        {
            int fps = LaunchFps(msg, toAppHome);
            if (fps <= 0)
            {
                // Unlock FPS is off. Roblox reads its Maximum Frame Rate from GlobalBasicSettings, which an earlier
                // launch may have set to 9999, so put it back to the normal 60 — otherwise turning Unlock FPS off
                // would leave Roblox uncapped anyway.
                Bootstrapper.SetFramerateCap(DefaultFps);
                Log.Write("Unlock FPS: off — Roblox's Maximum Frame Rate set back to " + DefaultFps);
                return;
            }
            Log.Write(toAppHome && Bool(msg, "menuCap") ? "Cap menu: opening the Roblox app home at " + fps + " FPS"
                : fps == UncappedFps ? "Unlock FPS: uncapped" : "Unlock FPS: capped at " + fps);
            Bootstrapper.SetFramerateCap(fps);
        }

        // The latest Roblox, or the installed one when the check fails. Only throws when there's neither.
        async Task<RobloxVersion> PickVersionAsync()
        {
            ReportProgress("Checking for Roblox updates…", 0.02);
            RobloxVersion latest = null;
            RateLimitedException limited = null;
            try { latest = await Bootstrapper.GetLatestAsync(); }
            catch (Exception ex)
            {
                Log.Write("Version check failed, using the installed copy: " + ex.Message);
                limited = ex as RateLimitedException;
            }
            RobloxVersion version = latest ?? Bootstrapper.GetInstalled();
            if (version == null)
            {
                if (limited != null) throw limited;
                throw new InvalidOperationException("Couldn't reach Roblox to download it. Check your internet connection and try again.");
            }
            if (limited != null)
                Toast("Roblox is limiting update checks right now, so Keystrap started the Roblox you already have.");
            return version;
        }

        // The saved sky, crosshair and mods, re-applied every launch so a freshly installed version gets them too.
        // Each one failing on its own only costs that one: Roblox still starts, with its own look for it.
        async Task ApplyCustomisationsAsync(string dir, Dictionary<string, object> msg)
        {
            string sky = msg.ContainsKey("sky") ? msg["sky"] as string : null;
            if (!string.IsNullOrEmpty(sky))
            {
                sky = Skies.Resolve(sky);
                ReportProgress(sky == Skies.Default ? "Checking Roblox's sky…" : "Applying your sky…", 0.95);
                await TryApplyAsync(() => Skies.ApplyTo(dir, sky, text => ReportProgress(text, 0.95)),
                    "the sky", "Keystrap couldn't apply your custom sky, so Roblox starts with its usual sky.");
            }
            await TryApplyAsync(() => Crosshairs.ApplyTo(dir),
                "the crosshair", "Keystrap couldn't apply your crosshair, so Roblox starts with its usual one.");
            await TryApplyAsync(() => Mods.ApplyTo(dir),
                "mods", "Keystrap couldn't apply your mods, so Roblox starts with its own font and sounds.");
        }

        async Task TryApplyAsync(Action apply, string what, string toast)
        {
            try { await Task.Run(apply); }
            catch (Exception ex)
            {
                Log.Write("Applying " + what + " failed: " + ex.Message);
                Toast(toast);
            }
        }

        static string LaunchErrorText(Exception ex)
        {
            if (ex is HttpRequestException || ex is WebException || ex is TaskCanceledException)
                return "Couldn't download Roblox. Check your internet connection and try again.";
            if (ex is IOException || ex is UnauthorizedAccessException)
                return "Couldn't write Roblox's files. Close Roblox if it's running, then try again.";
            return ex.Message;   // RateLimitedException's message already says how long to wait
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
