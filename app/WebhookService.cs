// Discord webhook notifications for the developers' channel, in a minimal changelog style: an author header,
// a title, a category tag, short text and bullets, a thin #FDE68A accent bar and a one-line footer.
// Shared by Keystrap.exe and Keystrap.Updater.exe, so it only uses what both builds reference
// (System.Net.Http and System.Web.Extensions) and C# 5.
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Keystrap
{
    enum NotificationKind { MinorUpdate, MajorUpdate, MinorFix, MajorFix, MinorCrash, MajorCrash }

    // One changelog-style message: a title, a line or two of text, "- **Name**: value" bullets and optionally a code
    // block (a stack trace, say), in that order.
    class Notification
    {
        public readonly NotificationKind Kind;
        public readonly string Title;
        public readonly string Description;
        public readonly List<string> Bullets = new List<string>();
        public string Code;

        public Notification(NotificationKind kind, string title, string description)
        {
            Kind = kind;
            Title = title;
            Description = description;
        }

        public Notification Field(string name, string value)
        {
            Bullets.Add("- **" + name + "**: " + (string.IsNullOrEmpty(value) ? "-" : value));
            return this;
        }

        public Notification Block(string code)
        {
            Code = code;
            return this;
        }
    }

    static class WebhookService
    {
        // From secrets.json (not in the repository); build.ps1 writes it into Secrets.g.cs. Empty: nothing is sent.
        const string Url = Secrets.NotificationsWebhook;

        // The small icon in the author header. Empty shows the header as text only; put a hosted image of the
        // Keystrap logo here (a direct .png link) to show it.
        const string AuthorIcon = "";

        // One accent for every message: the thin bar on the embed's left edge.
        const int Accent = 0xFDE68A;

        // Who is sending, for the User-Agent; each exe sets its own ("Keystrap v1", "Keystrap Updater v1").
        public static string Source = "Keystrap";

        class Style
        {
            public string Label, Tag, Header, Footer;
            public Style(string label, string tag, string header, string footer) { Label = label; Tag = tag; Header = header; Footer = footer; }
        }

        static readonly Dictionary<NotificationKind, Style> Styles = new Dictionary<NotificationKind, Style>
        {
            { NotificationKind.MinorUpdate, new Style("Minor update", "update", "Keystrap Changelog", "Keystrap Updates") },
            { NotificationKind.MajorUpdate, new Style("Major update", "update · major", "Keystrap Changelog", "Keystrap Updates") },
            { NotificationKind.MinorFix,    new Style("Minor fix", "fix", "Keystrap Changelog", "Keystrap Fixes") },
            { NotificationKind.MajorFix,    new Style("Major fix", "fix · major", "Keystrap Changelog", "Keystrap Fixes") },
            { NotificationKind.MinorCrash,  new Style("Minor crash", "error", "Keystrap Status", "Keystrap Crashes") },
            { NotificationKind.MajorCrash,  new Style("Major crash", "crash · major", "Keystrap Status", "Keystrap Crashes") },
        };

        public static string Label(NotificationKind kind) { return Styles[kind].Label; }

        public static string Clip(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        public static string BuildPayload(Notification n)
        {
            Style style = Styles[n.Kind];

            // `tag` line, the text, the bullets, then the code block, trimmed to fit Discord's 4096 characters.
            var body = new StringBuilder("`" + style.Tag + "`");
            if (!string.IsNullOrEmpty(n.Description)) body.Append("\n").Append(n.Description);
            if (n.Bullets.Count > 0) body.Append("\n\n").Append(string.Join("\n", n.Bullets));
            if (!string.IsNullOrEmpty(n.Code))
            {
                int room = 4000 - body.Length - 12;
                if (room > 200) body.Append("\n```\n").Append(Clip(n.Code.Replace("```", "'''"), room)).Append("\n```");
            }

            var author = new Dictionary<string, object> { { "name", style.Header } };
            if (AuthorIcon.Length > 0) author["icon_url"] = AuthorIcon;
            var embed = new Dictionary<string, object>
            {
                { "author", author },
                { "title", Clip(n.Title, 256) },
                { "description", Clip(body.ToString(), 4096) },
                { "color", Accent },
                // A plain one-line footer with the time written out (UTC, so reports from anywhere line up).
                { "footer", new Dictionary<string, object> { { "text", style.Footer + " | " + DateTime.UtcNow.ToString("MM/dd/yy, hh:mm tt", System.Globalization.CultureInfo.InvariantCulture) + " UTC" } } },
            };
            var payload = new Dictionary<string, object>
            {
                { "username", "Keystrap" },
                { "allowed_mentions", new Dictionary<string, object> { { "parse", new string[0] } } },   // never ping anyone
                { "embeds", new object[] { embed } },
            };
            if (AuthorIcon.Length > 0) payload["avatar_url"] = AuthorIcon;
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(payload);
        }

        // Posts in the background. True when Discord accepted it; never throws.
        public static async Task<bool> SendAsync(Notification n, TimeSpan timeout)
        {
            if (Url.Length == 0) { LastError = "no webhook set up (secrets.json)"; return false; }
            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = timeout;
                    http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent());
                    var body = new StringContent(BuildPayload(n), Encoding.UTF8, "application/json");
                    using (HttpResponseMessage resp = await http.PostAsync(Url, body).ConfigureAwait(false))
                    {
                        if (resp.IsSuccessStatusCode) return true;
                        LastError = "HTTP " + (int)resp.StatusCode;
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public static Task<bool> SendAsync(Notification n) { return SendAsync(n, TimeSpan.FromSeconds(15)); }

        // "Keystrap Updater v1.1" -> "Keystrap-Updater/v1.1": a User-Agent product token can't contain spaces or extra slashes.
        public static string UserAgent()
        {
            string s = (Source ?? "Keystrap").Trim();
            int space = s.LastIndexOf(' ');
            return space < 0 ? s.Replace('/', '-') : s.Substring(0, space).Replace(' ', '-').Replace('/', '-') + "/" + s.Substring(space + 1).Replace('/', '-');
        }

        // For a crashing process: waits at most `timeout` on a pool thread (so it can't deadlock the UI thread).
        public static bool Send(Notification n, TimeSpan timeout)
        {
            Task<bool> post = Task.Run(() => SendAsync(n, timeout));
            return post.Wait(timeout + TimeSpan.FromMilliseconds(250)) && post.Result;
        }

        // Why the last send failed, for the log.
        public static volatile string LastError;
    }
}
