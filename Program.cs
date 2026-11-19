using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ClaudeUsageWidget;

static class Program
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageWidget");
    // Written by statusline.js, which Claude Code runs as its status line command.
    public static readonly string UsageFile = Path.Combine(DataDir, "usage.json");
    public static readonly string SettingsFile = Path.Combine(DataDir, "settings.json");
    public static readonly string ExePath = Environment.ProcessPath!; // stays the launch path even after the file is moved aside
    public static bool RestartAfterExit;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest.Run();
        if (args.Contains("--statusline")) return StatusLine.Run(); // Claude Code runs this on every reply: no window, no waiting
        if (Installer.IsSetupCopy) { ApplicationConfiguration.Initialize(); return Installer.Run(); }
        UseLoginEnvironment();
        using (new Mutex(true, "ClaudeUsageWidget.SingleInstance", out bool first))
        {
            if (!first) return 0;
            ApplicationConfiguration.Initialize();
            Application.Run(new WidgetForm());
        }
        // After the mutex is gone, so the new copy doesn't think one is still running.
        if (RestartAfterExit) Process.Start(ExePath);
        return 0;
    }

    // Started from inside a Claude Code session (e.g. by Claude rebuilding it), the widget inherits that session's
    // variables (NO_COLOR, CLAUDECODE, its session id...), and so would every chat it opens: no colours, nested-session
    // behaviour. Swap them for the clean environment Windows gives a program started from Explorer.
    static void UseLoginEnvironment()
    {
        if (Environment.GetEnvironmentVariable("CLAUDECODE") == null) return;
        if (!Native.CreateEnvironmentBlock(out var block, System.Security.Principal.WindowsIdentity.GetCurrent().Token, false)) return;
        try
        {
            var fresh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // The block is "NAME=value\0" entries ending in an empty one. Names starting with '=' are hidden drive paths.
            for (var p = block; Marshal.PtrToStringUni(p) is { Length: > 0 } entry; p += (entry.Length + 1) * 2)
                if (entry.IndexOf('=') is var eq and > 0) fresh[entry[..eq]] = entry[(eq + 1)..];
            foreach (string name in Environment.GetEnvironmentVariables().Keys)
                if (!fresh.ContainsKey(name) && !name.StartsWith('=')) Environment.SetEnvironmentVariable(name, null);
            foreach (var (name, value) in fresh) Environment.SetEnvironmentVariable(name, value);
        }
        finally { Native.DestroyEnvironmentBlock(block); }
    }
}

record Limit(double Used, DateTimeOffset ResetsAt)
{
    public bool HasReset => DateTimeOffset.Now >= ResetsAt;
    // Claude Code stops sending a window once it resets, so until fresh data arrives a passed reset means 0%.
    public double UsedNow => HasReset ? 0 : Used;

    // When the limit fills at this window's average pace so far; null unless that comes before the reset.
    public DateTimeOffset? FullAt(TimeSpan window)
    {
        var elapsed = DateTimeOffset.Now - (ResetsAt - window);
        if (HasReset || Used is <= 0 or >= 100 || elapsed < window / 20) return null; // too early in the window to judge the pace
        var full = DateTimeOffset.Now + elapsed * ((100 - Used) / Used);
        return full < ResetsAt ? full : null;
    }
}

record Usage(Limit? Session, Limit? Week, DateTimeOffset UpdatedAt)
{
    public static Usage? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("rate_limits", out var limits)) return null;
        var updated = root.TryGetProperty("updated_at", out var u) && u.ValueKind == JsonValueKind.Number ? (long)u.GetDouble() : 0;
        return new Usage(Read(limits, "five_hour"), Read(limits, "seven_day"), DateTimeOffset.FromUnixTimeSeconds(updated));
    }

    static Limit? Read(JsonElement limits, string name) =>
        limits.ValueKind == JsonValueKind.Object && limits.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Object
        && w.TryGetProperty("used_percentage", out var p) && p.ValueKind == JsonValueKind.Number
        && w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number
            ? new Limit(Math.Clamp(p.GetDouble(), 0, 100), DateTimeOffset.FromUnixTimeSeconds((long)r.GetDouble()))
            : null;
}

static class Fmt
{
    public static string At(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        return local.Date == DateTime.Today ? $"{local:t}" : $"{local:ddd} {local:t}";
    }

    public static string Until(DateTimeOffset t)
    {
        var d = t - DateTimeOffset.Now;
        return d.TotalDays >= 1 ? $"{(int)d.TotalDays}d {d.Hours}h"
            : d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m"
            : $"{Math.Max(1, d.Minutes)}m";
    }

    public static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        return d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes}m ago"
            : d.TotalDays < 1 ? $"{(int)d.TotalHours}h ago" : $"{(int)d.TotalDays}d ago";
    }

    public static string Detail(string label, Limit? l, TimeSpan window) =>
        l == null ? $"{label} · no data" : l.HasReset ? $"{label} · reset, waiting for data"
        : l.FullAt(window) is { } full ? $"{label} · full ~{At(full)} · resets {At(l.ResetsAt)}"
        : $"{label} · resets {At(l.ResetsAt)} ({Until(l.ResetsAt)})";

    static string Short(string label, Limit? l) =>
        l == null ? $"{label}: no data" : l.HasReset ? $"{label}: reset" : $"{label} {l.Used:0}% · resets {At(l.ResetsAt)} ({Until(l.ResetsAt)})";

    public static string Percent(Limit? l, bool remaining) =>
        l == null ? "–" : remaining ? $"{100 - l.UsedNow:0}% left" : $"{l.UsedNow:0}%";

    public static string Tooltip(Usage? u, string? error)
    {
        var text = error ?? (u == null ? "Claude usage: no data yet" :
            $"Claude usage\n{Short("5h", u.Session)}\n{Short("7d", u.Week)}\nupdated {Ago(u.UpdatedAt)}");
        return text.Length > 127 ? text[..127] : text; // NotifyIcon.Text throws above 127 chars
    }
}

static class Theme
{
    // Two looks, switched from the menu: Warcraft III (dark wood, bronze trim, gold titles, parchment text) or the plain dark card.
    public static bool Warcraft = true;
    static Color Pick(Color warcraft, Color simple) => Warcraft ? warcraft : simple;
    public static Color Bg => Pick(Color.FromArgb(22, 17, 13), Color.FromArgb(31, 30, 29));
    public static Color Fg => Pick(Color.FromArgb(250, 240, 214), Color.FromArgb(243, 239, 232));
    public static Color Muted => Pick(Color.FromArgb(200, 178, 136), Color.FromArgb(163, 158, 147));
    public static Color Track => Pick(Color.FromArgb(70, 55, 36), Color.FromArgb(58, 55, 51));
    public static Color Panel => Pick(Color.FromArgb(38, 29, 21), Color.FromArgb(41, 39, 37));
    public static Color Gold => Pick(Color.FromArgb(255, 206, 50), Fg);     // titles and highlights
    public static Color Bronze => Pick(Color.FromArgb(132, 100, 52), Track); // trim

    static (double At, Color Color)[] Stops => Warcraft ? WarcraftStops : SimpleStops;
    static readonly (double At, Color Color)[] WarcraftStops =
    [
        // A unit's hit points, read backwards: healthy green when little is used, red when it's nearly gone.
        (0, Color.FromArgb(52, 186, 72)),    // green
        (50, Color.FromArgb(236, 196, 48)),  // gold
        (75, Color.FromArgb(230, 118, 34)),  // orange
        (100, Color.FromArgb(214, 46, 38)),  // red
    ];
    static readonly (double At, Color Color)[] SimpleStops =
    [
        (0, Color.FromArgb(108, 196, 161)),  // mint
        (50, Color.FromArgb(232, 193, 90)),  // soft yellow
        (75, Color.FromArgb(217, 119, 87)),  // Claude orange
        (100, Color.FromArgb(224, 76, 84)),  // soft red
    ];

    // Blends smoothly between the stops instead of jumping at thresholds.
    public static Color Level(double used)
    {
        used = Math.Clamp(used, 0, 100);
        int i = 1;
        while (i < Stops.Length - 1 && used > Stops[i].At) i++;
        var (from, to) = (Stops[i - 1], Stops[i]);
        double t = (used - from.At) / (to.At - from.At);
        int Mix(int a, int b) => (int)Math.Round(a + (b - a) * t);
        return Color.FromArgb(Mix(from.Color.R, to.Color.R), Mix(from.Color.G, to.Color.G), Mix(from.Color.B, to.Color.B));
    }

    // Fill that runs from the 0% colour up to the current level's colour.
    // Brush rect inflated by 1px: GDI+ gradients otherwise wrap a stray end-colour pixel onto the start edge.
    public static LinearGradientBrush Gradient(RectangleF fill, double used, bool vertical) => vertical
        ? new(RectangleF.Inflate(fill, 0, 1), Level(used), Level(0), LinearGradientMode.Vertical)
        : new(RectangleF.Inflate(fill, 1, 0), Level(0), Level(used), LinearGradientMode.Horizontal);
}

sealed class Settings
{
    public double Opacity { get; set; } = 0.92;
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool TopMost { get; set; } = true;
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public bool ClickThrough { get; set; }
    // Celebration
    public bool SoundOn { get; set; } = true;
    public int SoundVolume { get; set; } = 100;
    public string? SoundFile { get; set; } // null = the bundled confetti.mp3
    public bool ConfettiOn { get; set; } = true;
    public bool CelebrateSession { get; set; } = true;
    public bool CelebrateWeek { get; set; } = true;
    // Alerts
    public bool NotifyOnReset { get; set; } = true;
    public bool PulseHigh { get; set; } = true;
    // Display
    public bool ShowRemaining { get; set; }
    public bool Compact { get; set; }
    public bool ShowSession { get; set; } = true;
    public bool ShowWeek { get; set; } = true;
    public bool ShowChats { get; set; } = true;
    public bool WarnHigh { get; set; } = true;
    public bool Voices { get; set; } = true; // orc peon lines for chat and widget events
    public bool Warcraft { get; set; } = true; // the Warcraft III look; off is the plain dark card
    public string? LastFolder { get; set; } // where the last "+" chat started

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Program.SettingsFile)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Program.DataDir); File.WriteAllText(Program.SettingsFile, JsonSerializer.Serialize(this)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

// Warcraft III look: frames from warcraftcn/ui (MIT, github.com/TheOrcDev/warcraftcn-ui) and the Cinzel font (OFL), in art\.
// A missing file falls back to a plain dark card and Segoe UI.
static class Art
{
    static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "art");
    public static readonly Image? Card = Load("card.png"), Panel = Load("panel.png"), Button = Load("button.png"), Bar = Load("bar.png");
    // Frame thickness inside each image, in the image's own pixels (the card's top includes its hinges).
    public static readonly Padding CardEdge = new(40, 48, 40, 40), PanelEdge = new(76), BarEdge = new(41);
    public static readonly Cursor Cursor = LoadCursor(); // the orc gauntlet

    // Through Windows' own loader: WinForms' Cursor(file) drops a colour cursor's colours and shows it black.
    static Cursor LoadCursor()
    {
        var handle = Native.LoadCursorFromFile(Path.Combine(Dir, "orc.cur"));
        return handle != 0 ? new Cursor(handle) : Cursors.Default;
    }

    // Cinzel only has Latin letters; anything else (a chat named in Cyrillic, say) falls back to Segoe UI instead of boxes.
    public static bool Covers(string text) => Theme.Warcraft && text.All(c => c <= 0x17F || c is '–' or '—' or '‘' or '’' or '“' or '”' or '…' or '•' or '·');
    static readonly PrivateFontCollection fonts = new();
    public static readonly FontFamily Serif = LoadFonts();

    static Image? Load(string name)
    {
        try { return Image.FromFile(Path.Combine(Dir, name)); }
        catch (Exception e) when (e is IOException or OutOfMemoryException) { return null; } // missing, or not an image
    }

    static FontFamily LoadFonts()
    {
        foreach (var file in new[] { "Cinzel-Regular.ttf", "Cinzel-Bold.ttf" })
            if (File.Exists(Path.Combine(Dir, file))) fonts.AddFontFile(Path.Combine(Dir, file));
        return fonts.Families.FirstOrDefault() ?? new FontFamily("Segoe UI");
    }

    public static Font Font(float px, bool bold = false) => Theme.Warcraft
        ? new(Serif, px, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel)
        : new(bold ? "Segoe UI Semibold" : "Segoe UI", px, GraphicsUnit.Pixel);

    // Nine-slice: corners keep their shape, edges stretch along their length, the middle fills. k scales the frame.
    public static void Frame(Graphics g, Image? img, Rectangle d, Padding edge, float k, Color fallback)
    {
        if (img == null) { using var b = new SolidBrush(fallback); g.FillRectangle(b, d); return; }
        // On a box smaller than two frame edges, shrink the edges instead of overlapping them.
        float fx = Math.Min(1, d.Width / ((edge.Left + edge.Right) * k)), fy = Math.Min(1, d.Height / ((edge.Top + edge.Bottom) * k));
        int l = (int)(edge.Left * k * fx), r = (int)(edge.Right * k * fx), t = (int)(edge.Top * k * fy), b2 = (int)(edge.Bottom * k * fy);
        int[] dx = [d.Left, d.Left + l, d.Right - r, d.Right], dy = [d.Top, d.Top + t, d.Bottom - b2, d.Bottom];
        int[] sx = [0, edge.Left, img.Width - edge.Right, img.Width], sy = [0, edge.Top, img.Height - edge.Bottom, img.Height];
        using var clamp = new ImageAttributes();
        clamp.SetWrapMode(WrapMode.TileFlipXY); // no smeared seams between the pieces
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if (dx[i + 1] > dx[i] && dy[j + 1] > dy[j])
                    g.DrawImage(img, Rectangle.FromLTRB(dx[i], dy[j], dx[i + 1], dy[j + 1]),
                        sx[i], sy[j], sx[i + 1] - sx[i], sy[j + 1] - sy[j], GraphicsUnit.Pixel, clamp);
    }
}

// Dark wood menus with gold text.
sealed class Wc3MenuRenderer() : ToolStripProfessionalRenderer(new Colors())
{
    sealed class Colors : ProfessionalColorTable
    {
        static readonly Color Wood = Color.FromArgb(24, 18, 13), Hover = Color.FromArgb(70, 50, 26);
        public override Color ToolStripDropDownBackground => Wood;
        public override Color ImageMarginGradientBegin => Wood;
        public override Color ImageMarginGradientMiddle => Wood;
        public override Color ImageMarginGradientEnd => Wood;
        public override Color MenuBorder => Theme.Bronze;
        public override Color MenuItemBorder => Theme.Bronze;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Theme.Track;
        public override Color SeparatorLight => Wood;
        public override Color CheckBackground => Wood;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Fg : Theme.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Gold;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) // a gold tick instead of the black system one
    {
        using var gold = new SolidBrush(Theme.Gold);
        using var font = new Font("Segoe UI Symbol", e.ImageRectangle.Height * 0.8f, GraphicsUnit.Pixel);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString("✓", font, gold, e.ImageRectangle, center);
    }
}

// MCI plays mp3 with no extra dependency (SoundPlayer is wav-only). A missing file just stays silent.
static class Sound
{
    // Game sounds for chat and widget events: WoW interface sounds where they fit, Warcraft III orc lines for the rest
    // (both © Blizzard; the orc lines from the peon-ping project's pack, the WoW ones from wago.tools).
    public enum Voice { NewChat, Open, OpenFolder, NeedsInput, Failed, Removed, Busy, Annoyed, Explode, WeeklyReset, SessionReset,
        MenuOpen, MenuClose, CardShow, CardHide, OptionTick }
    static readonly Dictionary<Voice, string[]> Lines = new()
    {
        [Voice.NewChat] = ["PeonYes1"],            // the peon's "I can do that."
        [Voice.Open] = ["QuestLogOpen"],           // the quest log opening
        [Voice.OpenFolder] = ["BackpackOpen"],     // opening your bags
        [Voice.NeedsInput] = ["TellMessage"],      // a whisper
        [Voice.Failed] = ["QuestFailed"],
        [Voice.Removed] = ["PeonDeath"],           // the peon's death cry
        [Voice.Busy] = ["RaidWarning"],            // a limit passed 95%
        [Voice.Annoyed] = ["PeonAngry3"],          // "No time for play." (clicking the card over and over)
        [Voice.Explode] = ["explode"],             // the card bursting; ours, not the game's: swap sounds\explode.wav to change it
        [Voice.WeeklyReset] = ["QuestComplete"],   // the quest-complete fanfare
        [Voice.SessionReset] = ["LevelUp"],
        [Voice.MenuOpen] = ["MenuOpen"],
        [Voice.MenuClose] = ["MenuClose"],
        [Voice.CardShow] = ["CardShow"],           // the character sheet opening
        [Voice.CardHide] = ["CardHide"],
        [Voice.OptionTick] = ["OptionTick"],       // an options checkbox
    };
    // A shared copy carries no Blizzard audio and can only fetch the orc lines (the WoW sounds would need converting), so
    // where a WoW sound is missing an orc speaks instead; the plain clicks (menu, show/hide, tick) just stay quiet.
    static readonly Dictionary<Voice, string[]> Fallbacks = new()
    {
        [Voice.Open] = ["PeonWhat1", "PeonWhat3"],       // "Yes?" / "What you want?"
        [Voice.OpenFolder] = ["PeonYes4"],               // "Okie dokie."
        [Voice.NeedsInput] = ["PeonWhat4", "PeonWhat2"], // "Something need doing?" / "Hmm?"
        [Voice.Failed] = ["PeonAngry4"],                 // "Me not that kind of orc!"
        [Voice.Busy] = ["PeonAngry2"],                   // "Me busy, leave me alone!"
        [Voice.WeeklyReset] = ["GruntYesAttack1", "GruntYesAttack2", "GruntWarcry1"], // "For the Horde!" / "Lok'tar ogar!" / "WAAAAGH!"
        [Voice.SessionReset] = ["GruntYesAttack1", "GruntYesAttack2", "GruntWarcry1"],
    };
    // Clicks and fanfares play at once; spoken lines wait their turn, one at a time like the game.
    static readonly HashSet<Voice> Immediate = [Voice.Explode, Voice.WeeklyReset, Voice.SessionReset,
        Voice.MenuOpen, Voice.MenuClose, Voice.CardShow, Voice.CardHide, Voice.OptionTick];
    public static IEnumerable<string> VoiceFiles => Lines.Values.SelectMany(n => n).Select(n => Path.Combine(AppContext.BaseDirectory, "sounds", n + ".wav"));
    static readonly HashSet<string> openVoices = [];
    static long lastVoice = -10_000;

    public static void Say(Voice voice, int volume)
    {
        bool immediate = Immediate.Contains(voice);
        if (!immediate && Environment.TickCount64 - lastVoice < 1500) return;
        string[] Present(string[]? n) => [.. (n ?? []).Where(x => File.Exists(Path.Combine(AppContext.BaseDirectory, "sounds", x + ".wav")))];
        var names = Present(Lines[voice]) is { Length: > 0 } own ? own : Present(Fallbacks.GetValueOrDefault(voice));
        if (names.Length == 0) return;
        var name = names[Random.Shared.Next(names.Length)];
        var file = Path.Combine(AppContext.BaseDirectory, "sounds", name + ".wav");
        if (!openVoices.Contains(name))
        {
            if (Native.mciSendString($"open \"{file}\" type mpegvideo alias {name}", null, 0, 0) != 0) return;
            openVoices.Add(name);
        }
        if (!immediate) lastVoice = Environment.TickCount64;
        Native.mciSendString($"setaudio {name} volume to {Math.Clamp(volume, 0, 100) * 10}", null, 0, 0);
        Native.mciSendString($"play {name} from 0", null, 0, 0);
    }

    // The orc lines are Blizzard's, so a copy you hand on can leave them out: whatever is missing is fetched once from the
    // pack peon-ping uses (github.com/PeonPing/og-packs) and kept only if it matches the hash pinned here.
    static readonly Dictionary<string, (string Path, string Sha256)> Pack = new()
    {
        ["PeonYes1"] = ("v1.0.0/peon", "a644ec5632e566df6c057e124319884b5123eea5d5f54b04280b097c2231cc0b"),
        ["PeonYes4"] = ("v1.0.0/peon", "89b9f19fec29fde3b513cc98bcf1614a6d264850f65e6816a4483ac49dc34cc2"),
        ["PeonWhat1"] = ("v1.0.0/peon", "d4f5696d9db6875c80c8dccf95f6d8047a4564b214be90792b8a766e313106a3"),
        ["PeonWhat2"] = ("v1.0.0/peon", "51d3de60bbb1973f291b7059dc1a8c0d44ff7f524d31881c4ab790c5a2922ef2"),
        ["PeonWhat3"] = ("v1.0.0/peon", "4e8197030f01cbaef968ddb9566f19bfd68b3af622748238a3049d218bb64eed"),
        ["PeonWhat4"] = ("v1.0.0/peon", "6ee4f9d9f9d2fede1f953a196de8875dba2ffefa0c5c732adad3de45bbaf090e"),
        ["PeonAngry2"] = ("v1.0.0/peon", "bbf910617a548e55a45d2a7ebc74e45fa18e8bc6a3004f84e6e1af94434b146d"),
        ["PeonAngry3"] = ("v1.0.0/peon", "cf68875ec1600020d304c4b3c23a0116509acbf8e61d67d30c6d6ffcb9f13b69"),
        ["PeonAngry4"] = ("v1.0.0/peon", "39493727ee0dc6f134ee1c666c63240c6c9df77bceb8b529b549aa980d3849df"),
        ["PeonDeath"] = ("v1.0.0/peon", "c46dd952a4dca36cc3e71b5594d6b102ad8366380ca411190e38290090d258c8"),
        ["GruntYesAttack1"] = ("v1.4.0/wc3_grunt", "2bc29c2804d03eade2761919fac3d973c84c2e00d4a8c118dbe758b55756b4eb"),
        ["GruntYesAttack2"] = ("v1.4.0/wc3_grunt", "5f92175ece107330b4006e9e0a1c1aae10e9ddb59b5ebf3f8e889933811f3b50"),
        ["GruntWarcry1"] = ("v1.4.0/wc3_grunt", "3eadbf03a8dc0672f2c73d2f8ab422d91cecfe277a937dc1d7b0ddcc6a395162"),
    };

    // Every orc line and stand-in can be fetched, and the pack holds nothing the widget no longer plays.
    public static bool PackCoversLines
    {
        get
        {
            var orc = Lines.Values.Concat(Fallbacks.Values).SelectMany(n => n).Where(n => n.StartsWith("Peon") || n.StartsWith("Grunt")).ToHashSet();
            return orc.SetEquals(Pack.Keys);
        }
    }

    public static async Task FetchMissing()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "sounds");
        var missing = Pack.Where(p => !File.Exists(Path.Combine(dir, p.Key + ".wav"))).ToList();
        if (missing.Count == 0) return;
        try
        {
            Directory.CreateDirectory(dir);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            foreach (var (name, (path, sha)) in missing)
            {
                var bytes = await http.GetByteArrayAsync($"https://raw.githubusercontent.com/PeonPing/og-packs/{path}/sounds/{name}.wav");
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) == sha) await File.WriteAllBytesAsync(Path.Combine(dir, name + ".wav"), bytes);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException) { } // offline or read-only: quiet, retried next start
    }

    public static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "confetti.mp3");
    static string? openFile;

    // A custom file that's gone falls back to the bundled one.
    public static void Play(string? file, int volume)
    {
        file = File.Exists(file) ? file : FilePath;
        if (file != openFile)
        {
            Close();
            if (Native.mciSendString($"open \"{file}\" type mpegvideo alias pop", null, 0, 0) == 0) openFile = file;
        }
        if (openFile == null) return;
        Native.mciSendString($"setaudio pop volume to {Math.Clamp(volume, 0, 100) * 10}", null, 0, 0); // MCI volume is 0-1000
        Native.mciSendString("play pop from 0", null, 0, 0);
    }

    public static void Close()
    {
        if (openFile != null) Native.mciSendString("close pop", null, 0, 0);
        openFile = null;
        foreach (var name in openVoices) Native.mciSendString($"close {name}", null, 0, 0);
        openVoices.Clear();
    }
}

// Claude Code's status line command for an installed copy, so it needs no Node: the same job as statusline.js.
// Saves the plan's limits and each chat's context use for the widget, and prints a short status line.
static class StatusLine
{
    public static int Run()
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        var (usage, context, sessionId, line) = Parse(stdin.ReadToEnd());
        if (usage != null) Save(Program.UsageFile, usage);
        if (context != null) Save(Path.Combine(Chats.ContextDir, sessionId + ".json"), context);
        using var stdout = Console.OpenStandardOutput(); // a WinExe has no console, but piped output still works
        stdout.Write(Encoding.UTF8.GetBytes(line));
        return 0;
    }

    public static (JsonObject? Usage, JsonObject? Context, string SessionId, string Line) Parse(string input)
    {
        JsonObject? data = null;
        try { data = JsonNode.Parse(input) as JsonObject; } catch (JsonException) { }
        static JsonNode? At(JsonNode? node, string key) => node is JsonObject o ? o[key] : null;
        static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double d) ? d : null;
        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        static string Pct(double p) => $"{Math.Round(p, MidpointRounding.AwayFromZero)}%";

        var limits = At(data, "rate_limits") as JsonObject;
        var ctx = Num(At(At(data, "context_window"), "used_percentage"));
        var sessionId = Str(At(data, "session_id")) ?? "";
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var usage = limits == null ? null : new JsonObject { ["rate_limits"] = limits.DeepClone(), ["updated_at"] = now };
        // One file per chat, so parallel sessions never overwrite each other. The id becomes a file name, so it must be plain.
        var context = ctx is double c && Regex.IsMatch(sessionId, @"^[\w-]+$")
            ? new JsonObject { ["used_percentage"] = c, ["updated_at"] = now } : null;
        string?[] parts =
        [
            Str(At(At(data, "model"), "display_name")),
            ctx is double x ? $"ctx {Pct(x)}" : null,
            Num(At(At(limits, "five_hour"), "used_percentage")) is double h ? $"5h {Pct(h)}" : null,
            Num(At(At(limits, "seven_day"), "used_percentage")) is double w ? $"7d {Pct(w)}" : null,
        ];
        return (usage, context, sessionId, string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p))));
    }

    // Write then rename, so the widget never reads half a file (several sessions may write at once).
    static void Save(string file, JsonObject data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = $"{file}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, data.ToJsonString());
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // the line must still print if the file is busy
    }
}

// The one-file setup: an exe with no art\ beside it is the installer. After asking, it puts the widget in the user's
// programs folder (art and our own sounds come out of the exe; Blizzard's are fetched later), points Claude Code's status
// line at it, starts it with Windows and launches it.
static class Installer
{
    public static bool IsSetupCopy => !Directory.Exists(Path.Combine(AppContext.BaseDirectory, "art"));
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ClaudeUsageWidget");
    static string Exe => Path.Combine(Dir, "ClaudeUsageWidget.exe");
    static string SettingsPath => Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "settings.json");
    public static string StatusCommand(string exe) => $"\"{exe.Replace('\\', '/')}\" --statusline";
    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    // Claude Code's settings with the status line pointed at the widget and every other setting kept, and the command it replaces.
    public static (string Json, string? Replaced) WithStatusLine(string? settings, string command)
    {
        var root = (settings == null ? new JsonObject() : JsonNode.Parse(settings, null, Lenient) as JsonObject)
            ?? throw new JsonException("settings.json isn't a JSON object");
        var replaced = (root["statusLine"] as JsonObject)?["command"]?.ToString();
        root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = command };
        return (root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), replaced == command ? null : replaced);
    }

    public static int Run()
    {
        try
        {
            var settings = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;
            var (json, replaced) = WithStatusLine(settings, StatusCommand(Exe));
            var ask = "Install the Claude usage widget?\n\n" +
                $"• Into {Dir}\n" +
                "• Claude Code's status line will hand it your usage" +
                (replaced != null ? $", replacing your current one ({replaced}). Your settings are backed up first." : ".") + "\n" +
                "• It starts with Windows (switch that off in its menu).\n\n" +
                "Needs Claude Code on a Pro or Max plan.";
            if (MessageBox.Show(ask, "Claude usage widget", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return 0;

            Directory.CreateDirectory(Dir);
            // An installed copy may be running: Windows won't overwrite it but will rename it, and it restarts into the new one.
            if (File.Exists(Exe))
            {
                try { File.Delete(Exe + ".old"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                File.Move(Exe, Exe + ".old", overwrite: true);
            }
            File.Copy(Program.ExePath, Exe);
            var assembly = typeof(Installer).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("payload/")))
            {
                var target = Path.Combine(Dir, name["payload/".Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var from = assembly.GetManifestResourceStream(name)!;
                using var to = File.Create(target);
                from.CopyTo(to);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            if (settings != null) File.Copy(SettingsPath, SettingsPath + ".before-claude-usage-widget", overwrite: true);
            File.WriteAllText(SettingsPath, json);
            Startup.Set(true, Exe);
            Process.Start(Exe); // exits at once if a copy already runs; that one restarts into the new exe by itself
            MessageBox.Show("Installed and running. Your numbers appear after Claude Code's next reply.", "Claude usage widget");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or Win32Exception)
        {
            MessageBox.Show($"Couldn't install: {e.Message}", "Claude usage widget", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

// "Launch at Windows startup" is a value under HKCU\...\Run pointing at this exe.
// Task Manager's Startup apps switch is kept separately, under StartupApproved (first byte 2 = on).
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "ClaudeUsageWidget",
        ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    static string Command(string exe) => $"\"{exe}\"";

    // Ticked only when login starts this very exe (not an older copy elsewhere) and Task Manager hasn't switched it off.
    public static bool Enabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return string.Equals(run?.GetValue(Name) as string, Command(Program.ExePath), StringComparison.OrdinalIgnoreCase)
                && (approved?.GetValue(Name) is not byte[] { Length: > 0 } flags || flags[0] == 2);
        }
        set => Set(value, Program.ExePath);
    }

    public static void Set(bool on, string exe)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            if (on) run.SetValue(Name, Command(exe)); else run.DeleteValue(Name, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(Name, throwOnMissingValue: false); // no entry = on
    }
}

// Background Claude Code chats, from the CLI's scripting output (`claude agents --json --all`).
static class Chats
{
    public record Chat(string Id, string Name, string State, string Cwd, string SessionId = "", double? Context = null, string Detail = "");

    // statusline.js saves each chat's context use here, one file per session id.
    public static readonly string ContextDir = Path.Combine(Program.DataDir, "context");

    // Each CLI run costs ~0.2 s of CPU, so the widget re-reads only when a job's state file in here changes.
    public static readonly string JobsDir = Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "jobs");

    // Runs the CLI hidden; null if it couldn't start or hung. On a hang it kills only the CLI itself,
    // never its process tree, which can include the service that runs every background chat.
    public static async Task<(int Exit, string Out, string Err)?> Run(string folder, params string[] args)
    {
        using var p = new Process { StartInfo = new("claude", args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // a hung CLI must not block later refreshes
        try
        {
            p.Start();
            Task<string> stdout = p.StandardOutput.ReadToEndAsync(timeout.Token), stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            return (p.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            return null;
        }
        catch (Win32Exception) { return null; } // no claude on PATH
    }

    // The CLI's error is its last line (a sign-in warning can come first).
    public static string LastLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "claude didn't run";

    public static async Task<List<Chat>?> Load()
    {
        var r = await Run("", "agents", "--json", "--all");
        try { return r is { Exit: 0 } ? Parse(r.Value.Out) : null; }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; } // output format changed
    }

    public static List<Chat> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        // Only background chats can be opened with `claude attach`. The id goes on a command line, so it must be plain.
        return [.. doc.RootElement.EnumerateArray()
            .Where(s => Str(s, "kind") == "background")
            .Select(s => new Chat(Str(s, "id"), ShortName(Str(s, "name")), Str(s, "state"), Str(s, "cwd"), Str(s, "sessionId")))
            .Where(c => c.Id.Length > 0 && c.Id.All(char.IsAsciiLetterOrDigit))];
    }

    // An unnamed chat is named after its first prompt, which can be a whole pasted log (GDI+ can't draw ~40k chars).
    static string ShortName(string name)
    {
        var line = name.Split('\n', 2)[0].Trim();
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    // Null until the chat has refreshed its status line with the current statusline.js.
    public static double? ReadContext(Chat c)
    {
        if (c.SessionId.Length == 0 || !c.SessionId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')) return null; // it becomes a file name
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContextDir, c.SessionId + ".json")));
            return doc.RootElement.TryGetProperty("used_percentage", out var p) && p.ValueKind == JsonValueKind.Number ? Math.Clamp(p.GetDouble(), 0, 100) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    // shortcut: what the chat is doing now comes from Claude Code's internal job file, not a documented interface;
    // if its format changes the line just disappears. Switch over if `claude agents --json` starts reporting it.
    public static string ReadDetail(Chat c)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(JobsDir, c.Id, "state.json"))); // Id is checked plain in Parse
            return doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? ShortName(d.GetString()!) : "";
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return ""; }
    }

    public static string Tooltip(Chat c) =>
        $"{(c.Name.Length > 0 ? c.Name : c.Id)}\n{Status(c.State).Text} · " +
        (c.Context is double ctx ? $"context {ctx:0}%{(ctx >= 80 ? " (nearly full: /compact or start a new chat)" : "")}" : "context: no data yet") +
        (c.Detail.Length > 0 && c.Detail != c.Name ? $"\n{c.Detail}" : "") +
        $"\n{c.Cwd}\nClick: open · Middle-click: folder · Right-click: more";

    static bool IsOver(Chat c) => c.State is "done" or "stopped";

    // Running and waiting chats, plus ones that finished while the widget watched and haven't been opened since.
    // Needs-input first, then done, then working.
    public static List<Chat> Visible(List<Chat> all, HashSet<string> active, HashSet<string> finished)
    {
        foreach (var c in all)
            if (!IsOver(c)) active.Add(c.Id);
            else if (active.Remove(c.Id)) finished.Add(c.Id);
        return [.. all.Where(c => !IsOver(c) || finished.Contains(c.Id))
            .OrderBy(c => c.State switch { "blocked" => 0, "done" => 1, "working" or "running" => 3, _ => 2 })];
    }

    public static (string Text, Color Color) Status(string state) => state switch
    {
        "blocked" => ("needs input", Theme.Level(100)),
        "done" => ("done", Theme.Level(0)),
        "working" or "running" => ("working", Theme.Level(50)),
        _ => (state, Theme.Muted),
    };

    // A new PowerShell window in the chat's folder, attached to the chat; it stays open after you detach.
    public static void Open(Chat c) => Process.Start(new ProcessStartInfo("powershell.exe", $"-NoLogo -NoExit -Command claude attach {c.Id}")
        { UseShellExecute = true, WorkingDirectory = Directory.Exists(c.Cwd) ? c.Cwd : "" });
}

sealed class WidgetForm : Form
{
    readonly Settings settings = Settings.Load();
    readonly NotifyIcon tray = new() { Text = "Claude usage" };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer pulseTimer = new() { Interval = 40 }; // animates the pulse; runs only while a bar is above PulseAt
    readonly List<Action> menuRefreshers = []; // re-sync every check mark when a menu opens
    readonly ContextMenuStrip menu;
    const double PulseAt = 90;
    readonly FileSystemWatcher? jobsWatcher;
    readonly HashSet<string> activeChats = [], finishedChats = [];
    List<Chats.Chat> chats = []; // listed in the panel at the bottom of the card
    List<Chats.Chat> allChats = []; // every background chat, for the "+" box's folder list
    readonly Dictionary<string, double> lastUsed = []; // per limit, to warn when it crosses 80% / 95%
    readonly Dictionary<string, string> chatStates = []; // last seen state per chat, to voice changes
    string tipText = "";
    Bitmap? frameCache; // the frame images scaled to the card, redrawn only when its size or panel changes
    (Size, bool, int, bool) frameKey;
    volatile bool chatsDirty = true; // set from the watcher's thread
    bool chatsLoading;
    DateTime chatsLoadedAt;
    readonly ToolTip chatTip = new();
    string? hoveredChat;
    readonly DateTime exeStamp = File.GetLastWriteTimeUtc(Program.ExePath);
    DateTime exeSeen, exeSeenAt;
    Usage? usage;
    string? error;
    DateTime fileStamp;
    nint trayIconHandle;
    int trayLevel = int.MinValue;
    DateTime opacityHintUntil;
    bool firstShow = true;
    bool? sessionWasReset, weekWasReset; // null until known, so a window that reset long ago doesn't pop on launch

    float S => DeviceDpi / 96f;
    // Layout in unscaled pixels (×S on screen), set by the frame: the iron-bound card, or the thinner wood panel in compact mode.
    float CardWidth => settings.Compact ? 246 : 316;
    float Edge => Theme.Warcraft ? (settings.Compact ? 12 : 17) : (settings.Compact ? 6 : 8); // frame thickness at the sides and bottom
    float Top0 => Theme.Warcraft ? (settings.Compact ? 13 : 24) : (settings.Compact ? 7 : 12); // first line, under the frame top and its hinges
    float Pad => Edge + 6;                     // text inset
    RectangleF TitleButton(int k) => new(ClientSize.Width - (Edge + 4 + 25 * (k + 1)) * S, (Top0 - 3) * S, 22 * S, 22 * S);
    RectangleF HideBox => TitleButton(0);
    RectangleF ConfettiBox => TitleButton(1);
    RectangleF GearBox => TitleButton(2);
    RectangleF PlusBox => TitleButton(3);
    // Click-through needs a layered window, which WinForms only makes when Opacity < 1.
    double MaxOpacity => settings.ClickThrough ? 0.99 : 1;

    List<(string Label, string Short, Limit? Limit, TimeSpan Window)> Rows()
    {
        var rows = new List<(string, string, Limit?, TimeSpan)>();
        if (settings.ShowSession) rows.Add(("5-hour", "5h", usage?.Session, TimeSpan.FromHours(5)));
        if (settings.ShowWeek) rows.Add(("Weekly", "7d", usage?.Week, TimeSpan.FromDays(7)));
        return rows;
    }

    public static bool Crossed(double before, double now) => new[] { 80, 95 }.Any(t => before < t && now >= t);

    void Warn(string text) => tray.ShowBalloonTip(5000, "Claude usage", text.Length > 200 ? text[..200] + "…" : text, ToolTipIcon.Warning);

    // Height of the usage part in unscaled pixels; the chats panel goes below it.
    float BodyHeight => settings.Compact ? Top0 + 26 * Math.Max(1, Rows().Count) + 1 : Top0 + 42 + 44 * Math.Max(1, Rows().Count);
    // Limit i's loading bar, frame included, in screen pixels; the percentage sits to its right.
    RectangleF BarBox(int i) => settings.Compact
        ? new((Pad + 30) * S, (Top0 + 26 * i + 5) * S, ClientSize.Width - (2 * Pad + 30 + 56) * S, 15 * S)
        : new(Pad * S, (Top0 + 28 + 44 * i + 19) * S, ClientSize.Width - (2 * Pad + 56) * S, 16 * S);
    RectangleF PanelBox => new((Edge - 3) * S, BodyHeight * S, ClientSize.Width - 2 * (Edge - 3) * S, (25 * chats.Count + 16) * S);
    RectangleF ChatSlot(int i) => new(PanelBox.X + 8 * S, (BodyHeight + 8 + 25 * i) * S, PanelBox.Width - 16 * S, 25 * S);
    void Say(Sound.Voice voice) { if (settings.Voices && Theme.Warcraft) Sound.Say(voice, settings.SoundVolume); }

    // Menus in the game's serif in the Warcraft look; drop-downs don't inherit their parent's font, so each gets it.
    static void UseSerif(ToolStrip strip)
    {
        strip.Font = Theme.Warcraft ? new Font(Art.Serif, 10f) : SystemFonts.MenuFont!;
        foreach (var item in strip.Items.OfType<ToolStripMenuItem>()) UseSerif(item.DropDown);
    }

    // The window's colour key: frame-art pixels left transparent are painted this and Windows shows the desktop there.
    static readonly Color SeeThrough = Color.FromArgb(255, 0, 255);

    // Everything that differs between the two looks outside painting: menus, cursor, corners, see-through edges.
    void ApplyStyle()
    {
        ToolStripManager.Renderer = Theme.Warcraft ? new Wc3MenuRenderer() : new ToolStripProfessionalRenderer();
        UseSerif(menu);
        Cursor = Theme.Warcraft ? Art.Cursor : Cursors.Default;
        BackColor = Theme.Warcraft ? SeeThrough : Theme.Bg;
        TransparencyKey = Theme.Warcraft ? SeeThrough : Color.Empty;
        if (IsHandleCreated) RoundCorners();
        trayLevel = int.MinValue; // repaint the tray column in this look's colours
        if (Theme.Warcraft) _ = Sound.FetchMissing();
        ApplyLayout();
    }

    void RoundCorners()
    {
        int corners = Theme.Warcraft ? 1 : 2; // DWMWCP_DONOTROUND for the iron frame, DWMWCP_ROUND for the plain card (Windows 11)
        Native.DwmSetWindowAttribute(Handle, 33, ref corners, sizeof(int));
    }

    // Click the card over and over and, like a Warcraft III critter, it gets annoyed, then bursts, then comes back.
    readonly List<long> clicks = [];
    bool CountClick()
    {
        long now = Environment.TickCount64;
        clicks.RemoveAll(t => now - t > 2500);
        clicks.Add(now);
        if (!Theme.Warcraft) return false;
        if (clicks.Count == 4) Say(Sound.Voice.Annoyed);
        if (clicks.Count < 8) return false;
        clicks.Clear();
        Confetti.Pop(Bounds, S, gore: true);
        Say(Sound.Voice.Explode);
        Hide();
        var back = new System.Windows.Forms.Timer { Interval = 2000 };
        back.Tick += (_, _) =>
        {
            back.Dispose();
            if (!settings.Visible) return; // hidden to the tray meanwhile
            double target = Opacity;
            Opacity = 0;
            Show();
            var fade = new System.Windows.Forms.Timer { Interval = 30 };
            fade.Tick += (_, _) => { Opacity = Math.Min(target, Opacity + target / 12); if (Opacity >= target) fade.Dispose(); };
            fade.Start();
        };
        back.Start();
        return true;
    }
    Chats.Chat? ChatAt(Point p) => chats.Where((_, i) => ChatSlot(i).Contains(p)).FirstOrDefault();

    public static bool JustReset(bool? before, bool? now) => before == false && now == true;

    public WidgetForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        KeyPreview = true;
        Theme.Warcraft = settings.Warcraft;
        Text = "Claude usage";
        TopMost = settings.TopMost;
        Opacity = Math.Clamp(settings.Opacity, 0.2, MaxOpacity);
        ApplyLayout();
        Location = PlaceOnScreen();

        ContextMenuStrip = tray.ContextMenuStrip = menu = BuildMenu();
        ApplyStyle();
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleVisible(); };
        tray.Visible = true;
        timer.Tick += (_, _) => Reload();
        pulseTimer.Tick += (_, _) => Invalidate();
        if (Directory.Exists(Chats.JobsDir))
        {
            jobsWatcher = new(Chats.JobsDir, "state.json") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            FileSystemEventHandler dirty = (_, _) => chatsDirty = true;
            jobsWatcher.Changed += dirty;
            jobsWatcher.Created += dirty;
            jobsWatcher.Deleted += dirty;
            jobsWatcher.Renamed += (_, _) => chatsDirty = true;
            jobsWatcher.EnableRaisingEvents = true;
        }
        chatTip.OwnerDraw = true; // a dark, bronze-rimmed tooltip like the game's
        chatTip.Popup += (_, e) => e.ToolTipSize = TipSize(tipText);
        chatTip.Draw += (_, e) => DrawTip(e);
        timer.Start();
        Reload();
    }

    void ApplyLayout()
    {
        float panel = chats.Count == 0 ? 0 : 25 * chats.Count + 18;
        ClientSize = new Size((int)(CardWidth * S), (int)((BodyHeight + panel + Edge - 2) * S));
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        if (Bottom > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height); // a growing panel must stay on screen
        Invalidate();
    }

    async Task RefreshChats()
    {
        (chatsLoading, chatsDirty) = (true, false);
        var all = await Chats.Load();
        (chatsLoading, chatsLoadedAt) = (false, DateTime.Now);
        if (all == null || !settings.ShowChats) return; // keep the last list if the CLI failed
        if (VoiceForChanges(all, chatStates) is { } voice) Say(voice);
        allChats = all;
        chats = [.. Chats.Visible(all, activeChats, finishedChats)
            .Select(c => c with { Context = Chats.ReadContext(c), Detail = Chats.ReadDetail(c) })];
        ApplyLayout();
    }

    // A chat that just changed state gets a line; needing you beats failing beats finishing. Chats seen for the first time stay quiet.
    public static Sound.Voice? VoiceForChanges(List<Chats.Chat> all, Dictionary<string, string> seen)
    {
        Sound.Voice? voice = null;
        foreach (var c in all)
        {
            if (seen.TryGetValue(c.Id, out var was) && was != c.State)
                voice = c.State switch
                {
                    "blocked" => Sound.Voice.NeedsInput,
                    "failed" when voice != Sound.Voice.NeedsInput => Sound.Voice.Failed,
                    _ => voice,
                };
            seen[c.Id] = c.State;
        }
        return voice;
    }

    // Right-click on a chat: the rarer actions. Stop keeps the conversation; Remove deletes the background chat.
    void ShowChatMenu(Chats.Chat chat)
    {
        hoveredChat = null;
        chatTip.Hide(this);
        var m = new ContextMenuStrip();
        m.Items.AddRange([
            new ToolStripMenuItem("Open chat", null, (_, _) => OpenChat(chat)),
            new ToolStripMenuItem("Open folder", null, (_, _) => OpenChatFolder(chat)),
            new ToolStripMenuItem("Copy id", null, (_, _) => Clipboard.SetText(chat.Id)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Stop", null, (_, _) => _ = ChatCommand(chat, "stop")),
            new ToolStripMenuItem("Remove…", null, (_, _) =>
            {
                if (MessageBox.Show(this, $"Remove “{chat.Name}” from your background chats?", "Claude usage",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
                    _ = ChatCommand(chat, "rm");
            }),
        ]);
        m.Closed += (_, _) => { Say(Sound.Voice.MenuClose); BeginInvoke(m.Dispose); }; // dispose after the clicked item has run
        UseSerif(m);
        Say(Sound.Voice.MenuOpen);
        m.Show(Cursor.Position);
    }

    async Task ChatCommand(Chats.Chat chat, string verb)
    {
        var r = await Chats.Run("", verb, chat.Id);
        if (r is not { Exit: 0 }) Warn($"claude {verb} failed: {Chats.LastLine(r?.Err)}");
        else if (verb == "rm") Say(Sound.Voice.Removed);
        chatsDirty = true;
    }

    void NewChat()
    {
        var folders = new[] { settings.LastFolder ?? "" }.Concat(allChats.AsEnumerable().Reverse().Select(c => c.Cwd))
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        using var dialog = new NewChatForm(folders);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        settings.LastFolder = dialog.Folder;
        settings.Save();
        _ = StartChat(dialog.Prompt, dialog.Folder);
    }

    async Task StartChat(string prompt, string folder)
    {
        var r = await Chats.Run(folder, "--bg", prompt);
        if (r is not { Exit: 0 }) Warn($"Couldn't start the chat: {Chats.LastLine(r?.Err)}"); // e.g. a folder Claude doesn't trust yet
        else Say(Sound.Voice.NewChat);
        chatsDirty = true;
    }

    // Auto-update: a publish moves the running exe aside and writes a new one at the launch path (see the .csproj).
    // Restart into it once it has stopped changing for 3 s, so a half-written file never starts.
    void CheckForUpdate()
    {
        var stamp = File.Exists(Program.ExePath) ? File.GetLastWriteTimeUtc(Program.ExePath) : exeStamp;
        if (stamp != exeSeen) (exeSeen, exeSeenAt) = (stamp, DateTime.Now);
        else if (stamp != exeStamp && DateTime.Now - exeSeenAt > TimeSpan.FromSeconds(3))
        {
            Program.RestartAfterExit = true;
            Close();
        }
    }

    void OpenChat(Chats.Chat chat)
    {
        finishedChats.Remove(chat.Id); // opened, so it no longer needs to be listed as done
        chatsDirty = true;
        Say(Sound.Voice.Open);
        try { Chats.Open(chat); }
        catch (Win32Exception e) { Warn($"Couldn't open the chat: {e.Message}"); }
    }

    // The chat's project folder in Explorer. Checked to be a folder first, so the shell never runs a file.
    void OpenChatFolder(Chats.Chat chat)
    {
        try
        {
            if (Directory.Exists(chat.Cwd))
            {
                Say(Sound.Voice.OpenFolder);
                Process.Start(new ProcessStartInfo(chat.Cwd) { UseShellExecute = true });
            }
            else Warn($"Folder not found: {chat.Cwd}");
        }
        catch (Win32Exception e) { Warn($"Couldn't open the folder: {e.Message}"); }
    }

    Point PlaceOnScreen()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var fallback = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        if (settings.X is not int x || settings.Y is not int y) return fallback;
        var saved = new Rectangle(x, y, Width, Height);
        return Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved)) ? saved.Location : fallback; // monitor may be gone
    }

    // A checked menu item bound to a setting. Any click saves and re-lays out the card.
    ToolStripMenuItem Toggle(string text, Func<bool> get, Action<bool> set)
    {
        var item = new ToolStripMenuItem(text) { Checked = get() };
        item.Click += (_, _) => { set(!get()); settings.Save(); RefreshMenu(); ApplyLayout(); Say(Sound.Voice.OptionTick); };
        menuRefreshers.Add(() => item.Checked = get());
        return item;
    }

    void RefreshMenu() { foreach (var refresh in menuRefreshers) refresh(); }

    static ToolStripMenuItem Submenu(string text, params ToolStripItem[] items)
    {
        var sub = new ToolStripMenuItem(text);
        sub.DropDownItems.AddRange(items);
        return sub;
    }

    ContextMenuStrip BuildMenu()
    {
        var slider = new TrackBar { Minimum = 0, Maximum = 100, TickFrequency = 10, AutoSize = false, Width = (int)(180 * S), Height = (int)(32 * S),
            Value = Math.Clamp(settings.SoundVolume, 0, 100) };
        menuRefreshers.Add(() => slider.BackColor = Theme.Warcraft ? Color.FromArgb(24, 18, 13) : SystemColors.Control);
        var volume = Submenu($"Sound volume: {slider.Value}%", new ToolStripControlHost(slider));
        slider.ValueChanged += (_, _) => { settings.SoundVolume = slider.Value; volume.Text = $"Sound volume: {slider.Value}%"; settings.Save(); };
        var customSound = Toggle("Choose file…", () => settings.SoundFile != null, _ => PickSound());
        menuRefreshers.Add(() => customSound.Text = settings.SoundFile is string f ? $"Custom: {Path.GetFileName(f)} (change…)" : "Choose file…");
        var defaultSound = Toggle("Default", () => settings.SoundFile == null, _ => settings.SoundFile = null);
        menuRefreshers.Add(() => defaultSound.Text = Theme.Warcraft ? "Default (level up / quest complete)" : "Default (confetti pop)");
        var soundFile = Submenu("Sound file", defaultSound, customSound);
        var opacity = Submenu("Opacity", [.. new[] { 100, 90, 80, 70, 60, 50, 40, 30, 20 }.Select(pct =>
            Toggle($"{pct}%", () => (int)Math.Round(Opacity * 100) == pct, _ => SetOpacity(pct / 100.0)))]);
        opacity.DropDownItems.Add(new ToolStripMenuItem("Tip: scroll on the widget") { Enabled = false });
        var toggle = new ToolStripMenuItem("Hide to tray", null, (_, _) => ToggleVisible());

        var menu = new ContextMenuStrip();
        menu.Opening += (_, e) =>
        {
            // Right-click on a chat row gets that chat's menu instead.
            if (Visible && Bounds.Contains(Cursor.Position) && ChatAt(PointToClient(Cursor.Position)) is { } chat)
            {
                e.Cancel = true;
                ShowChatMenu(chat);
                return;
            }
            toggle.Text = Visible ? "Hide to tray" : "Show widget";
            Say(Sound.Voice.MenuOpen);
            RefreshMenu();
        };
        menu.Closed += (_, _) => Say(Sound.Voice.MenuClose);
        menu.Items.AddRange([
            new ToolStripMenuItem("New chat…", null, (_, _) => NewChat()),
            new ToolStripSeparator(),
            Submenu("Celebration",
                Toggle("Sound", () => settings.SoundOn, v => settings.SoundOn = v),
                soundFile,
                volume,
                Toggle("Confetti", () => settings.ConfettiOn, v => settings.ConfettiOn = v),
                new ToolStripSeparator(),
                Toggle("On 5-hour reset", () => settings.CelebrateSession, v => settings.CelebrateSession = v),
                Toggle("On weekly reset", () => settings.CelebrateWeek, v => settings.CelebrateWeek = v),
                new ToolStripSeparator(),
                new ToolStripMenuItem("Test celebration", null, (_, _) => Celebrate(weekly: false))),
            Submenu("Display",
                Toggle("Show remaining instead of used", () => settings.ShowRemaining, v => settings.ShowRemaining = v),
                Toggle("Compact mode", () => settings.Compact, v => settings.Compact = v),
                new ToolStripSeparator(),
                // Hiding one limit shows the other, so the card is never empty.
                Toggle("Show 5-hour limit", () => settings.ShowSession, v => { settings.ShowSession = v; settings.ShowWeek |= !v; }),
                Toggle("Show weekly limit", () => settings.ShowWeek, v => { settings.ShowWeek = v; settings.ShowSession |= !v; }),
                Toggle("Show chats panel", () => settings.ShowChats, v => { settings.ShowChats = v; chats = []; chatsDirty = true; }),
                new ToolStripSeparator(),
                Toggle("Warcraft III look", () => settings.Warcraft, v => { settings.Warcraft = Theme.Warcraft = v; ApplyStyle(); })),
            Submenu("Alerts",
                Toggle("Notify when a limit resets", () => settings.NotifyOnReset, v => settings.NotifyOnReset = v),
                Toggle("Warn at 80% and 95%", () => settings.WarnHigh, v => settings.WarnHigh = v),
                Toggle("Game sounds", () => settings.Voices, v => settings.Voices = v),
                Toggle($"Pulse bar above {PulseAt}%", () => settings.PulseHigh, v => settings.PulseHigh = v)),
            new ToolStripSeparator(),
            opacity,
            Toggle("Always on top", () => settings.TopMost, v => TopMost = settings.TopMost = v),
            Toggle("Lock position", () => settings.Locked, v => settings.Locked = v),
            Toggle("Click-through (turn off from the tray)", () => settings.ClickThrough, SetClickThrough),
            Toggle("Launch at Windows startup", () => Startup.Enabled, v => Startup.Enabled = v),
            new ToolStripSeparator(),
            toggle,
            new ToolStripMenuItem("Exit", null, (_, _) => Close()),
        ]);
        return menu;
    }

    void PickSound()
    {
        using var dialog = new OpenFileDialog { Title = "Celebration sound", Filter = "Audio (*.mp3;*.wav;*.wma)|*.mp3;*.wav;*.wma|All files|*.*" };
        if (dialog.ShowDialog() == DialogResult.OK) settings.SoundFile = dialog.FileName;
    }

    void SetClickThrough(bool on)
    {
        settings.ClickThrough = on;
        if (Opacity > MaxOpacity) SetOpacity(MaxOpacity);
        UpdateStyles(); // re-applies CreateParams, which adds WS_EX_TRANSPARENT
    }

    void Reload()
    {
        try
        {
            var stamp = File.Exists(Program.UsageFile) ? File.GetLastWriteTimeUtc(Program.UsageFile) : default;
            if (stamp != fileStamp)
            {
                usage = stamp == default ? null : Usage.Parse(ReadShared(Program.UsageFile));
                fileStamp = stamp;
            }
            error = null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            error = "Can't read usage file, retrying"; // stamp not updated, so the next tick retries
        }
        bool? session = usage?.Session?.HasReset, week = usage?.Week?.HasReset;
        bool sessionJust = JustReset(sessionWasReset, session), weekJust = JustReset(weekWasReset, week);
        if (sessionJust && settings.CelebrateSession || weekJust && settings.CelebrateWeek) Celebrate(weekly: weekJust && settings.CelebrateWeek);
        if ((sessionJust || weekJust) && settings.NotifyOnReset)
            tray.ShowBalloonTip(5000, "Claude usage", sessionJust && weekJust ? "5-hour and weekly limits reset"
                : sessionJust ? "5-hour limit reset" : "Weekly limit reset", ToolTipIcon.Info);
        (sessionWasReset, weekWasReset) = (session, week);
        foreach (var (label, limit) in new[] { ("5-hour", usage?.Session), ("Weekly", usage?.Week) })
        {
            if (limit == null) continue;
            if (settings.WarnHigh && lastUsed.TryGetValue(label, out var before) && Crossed(before, limit.UsedNow))
            {
                Warn($"{label} limit at {limit.UsedNow:0}%");
                if (before < 95 && limit.UsedNow >= 95) Say(Sound.Voice.Busy); // 80% is a note, not a siren
            }
            lastUsed[label] = limit.UsedNow; // first sight only records, so launching at 90% doesn't warn
        }
        pulseTimer.Enabled = settings.PulseHigh && Visible && Rows().Any(r => r.Limit?.UsedNow >= PulseAt);
        var sinceChats = DateTime.Now - chatsLoadedAt;
        if (settings.ShowChats && !chatsLoading && (chatsDirty && sinceChats.TotalSeconds >= 2 || sinceChats.TotalSeconds >= 60))
            _ = RefreshChats();
        CheckForUpdate();
        UpdateTray();
        if (Visible) Invalidate();
    }

    // Burst out of the whole card, or out of a strip by the tray when the card is hidden.
    // weekly: the weekly limit reset (the bigger fanfare); otherwise the 5-hour one.
    void Celebrate(bool weekly)
    {
        if (settings.ConfettiOn)
        {
            var area = Screen.FromControl(this).WorkingArea;
            var spawn = Visible ? Bounds : new Rectangle(area.Right - (int)(160 * S), area.Bottom - (int)(20 * S), (int)(140 * S), (int)(20 * S));
            Confetti.Pop(spawn, S);
        }
        if (!settings.SoundOn) return;
        if (Theme.Warcraft && settings.SoundFile == null) // quest complete for the week, level up for 5 hours
            Sound.Say(weekly ? Sound.Voice.WeeklyReset : Sound.Voice.SessionReset, settings.SoundVolume);
        else Sound.Play(settings.SoundFile, settings.SoundVolume);
    }

    // Share Delete so statusline.js can replace the file while we read it.
    static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    void UpdateTray()
    {
        tray.Text = Fmt.Tooltip(usage, error);
        double? used = usage?.Session?.UsedNow;
        int level = used is double u ? (int)Math.Round(u) : -1;
        if (level == trayLevel) return;
        trayLevel = level;

        // A vertical column that fills up with the 5-hour session usage.
        var size = SystemInformation.SmallIconSize;
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            int w = Math.Max(6, size.Width * 9 / 16), x = (size.Width - w) / 2, h = size.Height;
            using var track = new SolidBrush(Color.FromArgb(235, 32, 31, 30));
            g.FillRectangle(track, x, 0, w, h);
            if (used is double v && v > 0)
            {
                int fill = (int)Math.Ceiling((h - 2) * v / 100);
                var column = new RectangleF(x + 1, h - 1 - fill, w - 2, fill);
                using var brush = Theme.Gradient(column, v, vertical: true);
                g.FillRectangle(brush, column);
            }
            using var outline = new Pen(Theme.Fg);
            g.DrawRectangle(outline, x, 0, w - 1, h - 1);
        }
        var handle = bmp.GetHicon();
        var old = tray.Icon;
        tray.Icon = Icon.FromHandle(handle);
        old?.Dispose();
        if (trayIconHandle != 0) Native.DestroyIcon(trayIconHandle); // Icon.FromHandle doesn't own the handle
        trayIconHandle = handle;
    }

    void ToggleVisible()
    {
        Say(Visible ? Sound.Voice.CardHide : Sound.Voice.CardShow);
        if (Visible) Hide(); else { Show(); Activate(); }
        settings.Visible = Visible;
        settings.Save();
    }

    void SetOpacity(double value)
    {
        Opacity = Math.Clamp(Math.Round(value, 2), 0.2, MaxOpacity);
        settings.Opacity = Opacity;
        settings.Save();
        opacityHintUntil = DateTime.Now.AddSeconds(1.5);
        Invalidate();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: keep it out of Alt+Tab
            if (settings.ClickThrough) cp.ExStyle |= 0x20; // WS_EX_TRANSPARENT: clicks pass through (layered windows only)
            return cp;
        }
    }

    protected override void SetVisibleCore(bool value)
    {
        if (firstShow)
        {
            firstShow = false;
            if (!settings.Visible) { CreateHandle(); value = false; } // it was hidden in the tray when you quit
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RoundCorners();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Middle && ChatAt(e.Location) is { } project) { OpenChatFolder(project); return; }
        if (e.Button == MouseButtons.Middle && e.Y < BodyHeight * S) // the usage part, percentages included
        {
            settings.Compact = !settings.Compact;
            settings.Save();
            ApplyLayout();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (ChatAt(e.Location) is { } chat) { OpenChat(chat); return; }
        if (!settings.Compact) // compact mode has no buttons
        {
            if (HideBox.Contains(e.Location)) { ToggleVisible(); return; }
            if (ConfettiBox.Contains(e.Location)) { Celebrate(weekly: false); return; }
            if (PlusBox.Contains(e.Location)) { NewChat(); return; }
            if (GearBox.Contains(e.Location)) { menu.Show(this, new Point((int)GearBox.Left, (int)GearBox.Bottom)); return; }
        }
        if (CountClick()) return; // it just burst
        if (settings.Locked) return;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, 0xA1, 2, 0); // WM_NCLBUTTONDOWN on HTCAPTION: drag the borderless window
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var chat = ChatAt(e.Location);
        if (chat?.Id == hoveredChat) return;
        hoveredChat = chat?.Id;
        if (chat == null) chatTip.Hide(this);
        else ShowChatTip(chat);
    }

    // Beside the card, never over it: a tip that lands under the cursor takes the mouse, hides itself, and flickers.
    void ShowChatTip(Chats.Chat chat)
    {
        tipText = Chats.Tooltip(chat);
        int width = TipSize(tipText).Width, gap = (int)(6 * S);
        var area = Screen.FromControl(this).WorkingArea;
        int x = Right + gap + width <= area.Right ? ClientSize.Width + gap : -width - gap;
        chatTip.Show(tipText, this, x, (int)ChatSlot(chats.IndexOf(chat)).Y);
    }

    Font TipFont(string text) => Art.Covers(text) ? Art.Font(13.5f * S) : new("Segoe UI", 13.5f * S, GraphicsUnit.Pixel);

    // GDI+ throughout: TextRenderer can't use Cinzel, a private font.
    Size TipSize(string text)
    {
        using var f = TipFont(text);
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        var size = g.MeasureString(text, f);
        return new Size((int)(size.Width + 18 * S), (int)(size.Height + 12 * S));
    }

    // The chat's name in gold, the rest in parchment, on near-black with a bronze rim.
    void DrawTip(DrawToolTipEventArgs e)
    {
        using (var bg = new SolidBrush(Theme.Warcraft ? Color.FromArgb(14, 10, 7) : Theme.Panel)) e.Graphics.FillRectangle(bg, e.Bounds);
        using (var rim = new Pen(Theme.Bronze)) e.Graphics.DrawRectangle(rim, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
        var text = e.ToolTipText ?? "";
        using var f = TipFont(text);
        var lines = text.Split('\n', 2);
        float x = 9 * S, y = 6 * S;
        e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var gold = new SolidBrush(Theme.Gold)) e.Graphics.DrawString(lines[0], f, gold, x, y);
        if (lines.Length > 1)
            using (var fg = new SolidBrush(Theme.Fg)) e.Graphics.DrawString(lines[1], f, fg, x, y + e.Graphics.MeasureString(lines[0], f).Height);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hoveredChat = null;
        chatTip.Hide(this);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        (settings.X, settings.Y) = (Left, Top);
        settings.Save();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        SetOpacity(Opacity + Math.Sign(e.Delta) * 0.05);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) ToggleVisible();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = S, pad = Pad * s, right = ClientSize.Width - pad;

        // The background (frames are images, slow to scale) is drawn once per card size and look into a cached bitmap.
        var key = (ClientSize, settings.Compact, chats.Count, Theme.Warcraft);
        if (frameCache == null || frameKey != key)
        {
            frameCache?.Dispose();
            frameCache = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
            using (var fg2 = Graphics.FromImage(frameCache))
            {
                fg2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                fg2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                fg2.SmoothingMode = SmoothingMode.AntiAlias;
                if (Theme.Warcraft)
                {
                    fg2.Clear(Color.Transparent);
                    if (settings.Compact) Art.Frame(fg2, Art.Panel, ClientRectangle, Art.PanelEdge, 0.16f * s, Theme.Bg);
                    else Art.Frame(fg2, Art.Card, ClientRectangle, Art.CardEdge, 0.42f * s, Theme.Bg);
                    if (chats.Count > 0) Art.Frame(fg2, Art.Panel, Rectangle.Round(PanelBox), Art.PanelEdge, 0.16f * s, Theme.Panel);
                    for (int i = 0; i < Rows().Count; i++) // the loading bars: a black well in a riveted iron frame (warcraftcn's input frame)
                    {
                        var box = BarBox(i);
                        fg2.FillRectangle(Brushes.Black, RectangleF.Inflate(box, -box.Height / 8, -box.Height / 8));
                        Art.Frame(fg2, Art.Bar, Rectangle.Round(box), Art.BarEdge, box.Height / 4 / Art.BarEdge.Top, Color.Black);
                    }
                }
                else
                {
                    fg2.Clear(Theme.Bg);
                    if (chats.Count > 0) using (var path = Rounded(PanelBox, 10 * s)) using (var b = new SolidBrush(Theme.Panel)) fg2.FillPath(b, path);
                }
            }
            if (Theme.Warcraft) KeyOutTransparency(frameCache);
            frameKey = key;
        }
        g.DrawImageUnscaled(frameCache, 0, 0);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        // Pixel units scaled by this monitor's DPI (point sizes would follow the primary monitor only).
        using var titleFont = Art.Font(15.5f * s, bold: true);
        using var pctFont = Art.Font(16 * s, bold: true);
        using var textFont = Art.Font(12 * s);
        using var smallFont = Art.Font(11 * s);
        using var nameFont = Art.Font(13 * s);
        using var nameFallback = new Font("Segoe UI", 13 * s, GraphicsUnit.Pixel); // for names Cinzel can't spell
        using var symbolFont = new Font("Segoe UI Symbol", 13.5f * s, GraphicsUnit.Pixel);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var rows = Rows();
        // A limit that will fill before it resets is written in orange.
        Color RowColor(int i) => rows[i].Limit?.FullAt(rows[i].Window) != null ? Theme.Level(75) : Theme.Muted;

        // Text with the game's dark drop shadow, so it reads on the wood.
        void Write(string text, Font font, Color color, float x, float y)
        {
            using var shadow = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
            using var brush = new SolidBrush(color);
            if (Theme.Warcraft) g.DrawString(text, font, shadow, x + s, y + s);
            g.DrawString(text, font, brush, x, y);
        }

        void DrawPercent(Limit? limit, float y)
        {
            var pct = Fmt.Percent(limit, settings.ShowRemaining);
            Write(pct, pctFont, Theme.Fg, right - g.MeasureString(pct, pctFont).Width, y);
        }

        // The plain card's bar: a rounded track, filled with the usage colour.
        void DrawPill(RectangleF bar, Limit? limit)
        {
            var track = new RectangleF(bar.X, bar.Y + bar.Height / 2 - 4 * s, bar.Width, 8 * s);
            using (var path = Rounded(track, track.Height)) using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, path);
            if (limit is not { UsedNow: > 0 } l) return;
            var fill = track with { Width = Math.Max(track.Height, track.Width * (float)(l.UsedNow / 100)) };
            using var fillPath = Rounded(fill, fill.Height);
            using (var b = Theme.Gradient(fill, l.UsedNow, vertical: false)) g.FillPath(b, fillPath);
            if (settings.PulseHigh && l.UsedNow >= PulseAt)
            {
                double phase = 0.5 + 0.5 * Math.Sin(Environment.TickCount64 / 1000.0 * Math.PI * 2 * 0.8); // 0.8 Hz
                using var dim = new SolidBrush(Color.FromArgb((int)(140 * phase), Theme.Bg));
                g.FillPath(dim, fillPath);
            }
        }

        // Warcraft III loading bar. Its iron frame is in the cached background; inside it goes a glowing fill:
        // the usage colour, lit along the top and shaded along the bottom like a tube, with a bright leading edge.
        void DrawBar(RectangleF bar, Limit? limit)
        {
            if (!Theme.Warcraft) { DrawPill(bar, limit); return; }
            float t = bar.Height / 4; // the frame's thickness
            var inner = RectangleF.Inflate(bar, -t, -t);
            if (Art.Bar == null) using (var well = new SolidBrush(Color.Black)) g.FillRectangle(well, inner);
            if (limit is not { UsedNow: > 0 } l) return;
            var fill = inner with { Width = Math.Max(2 * s, inner.Width * (float)(l.UsedNow / 100)) };
            using (var b = Theme.Gradient(fill, l.UsedNow, vertical: false)) g.FillRectangle(b, fill);
            using (var tube = new LinearGradientBrush(RectangleF.Inflate(fill, 0, 1), Color.Black, Color.Black, LinearGradientMode.Vertical)
            {
                InterpolationColors = new ColorBlend
                {
                    Colors = [Color.FromArgb(120, 255, 255, 230), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(130, 0, 0, 0)],
                    Positions = [0f, 0.45f, 1f],
                },
            }) g.FillRectangle(tube, fill);
            var tip = fill with { X = fill.Right - Math.Min(fill.Width, 8 * s), Width = Math.Min(fill.Width, 8 * s) };
            using (var glow = new LinearGradientBrush(RectangleF.Inflate(tip, 1, 0), Color.FromArgb(0, 255, 250, 220), Color.FromArgb(200, 255, 250, 220), LinearGradientMode.Horizontal))
                g.FillRectangle(glow, tip);
            if (settings.PulseHigh && l.UsedNow >= PulseAt)
            {
                double phase = 0.5 + 0.5 * Math.Sin(Environment.TickCount64 / 1000.0 * Math.PI * 2 * 0.8); // 0.8 Hz
                using var dim = new SolidBrush(Color.FromArgb((int)(140 * phase), Theme.Bg));
                g.FillRectangle(dim, fill);
            }
        }

        if (chats.Count > 0)
        {
            using var ellipsis = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
            using var ringFont = Art.Font(9 * s, bold: true);
            using var trackPen = new Pen(Color.FromArgb(200, 6, 5, 4), 2.5f * s);
            using var fgBrush = new SolidBrush(Theme.Fg);
            for (int i = 0; i < chats.Count; i++)
            {
                var (slot, chat) = (ChatSlot(i), chats[i]);
                var (status, color) = Chats.Status(chat.State);
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, slot.X + 4 * s, slot.Y + 8.5f * s, 8 * s, 8 * s);

                // Context ring on the right: fills and turns red as the chat's context fills up.
                var ring = new RectangleF(slot.Right - 22 * s, slot.Y + 2 * s, 21 * s, 21 * s);
                if (chat.Context is double ctx)
                {
                    g.DrawEllipse(trackPen, ring);
                    using var arc = new Pen(Theme.Level(ctx), 2 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    if (ctx >= 1) g.DrawArc(arc, ring, -90, (float)(3.6 * ctx));
                    g.DrawString($"{ctx:0}", ringFont, fgBrush, RectangleF.Inflate(ring, 2 * s, 0), center);
                }

                float statusWidth = g.MeasureString(status, smallFont).Width;
                Write(status, smallFont, color, ring.Left - 3 * s - statusWidth, slot.Y + 5.5f * s);
                var nameBox = new RectangleF(slot.X + 17 * s, slot.Y + 3 * s, ring.Left - 7 * s - statusWidth - (slot.X + 17 * s), 20 * s);
                var name = chat.Name.Length > 0 ? chat.Name : chat.Id;
                g.DrawString(name, Art.Covers(name) ? nameFont : nameFallback, fgBrush, nameBox, ellipsis);
            }
        }

        if (settings.Compact)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                float y = (Top0 + 26 * i) * s;
                Write(rows[i].Short, textFont, RowColor(i), pad, y + 2 * s);
                DrawPercent(rows[i].Limit, y);
                DrawBar(BarBox(i), rows[i].Limit);
            }
            return;
        }

        Write(Theme.Warcraft ? "Claude Usage" : "Claude usage", titleFont, Theme.Gold, pad, (Top0 - 1) * s);
        // Small command buttons, like the game's: a dark well, a bronze rim and a gold glyph.
        foreach (var (box, glyph) in new[] { (HideBox, "–"), (ConfettiBox, "✦"), (GearBox, "⚙"), (PlusBox, "+") })
        {
            if (!Theme.Warcraft) // the plain card: bare glyphs, the sparkle in Claude orange
            {
                using var plain = new SolidBrush(glyph == "✦" ? Theme.Level(75) : Theme.Muted);
                g.DrawString(glyph, symbolFont, plain, box, center);
                continue;
            }
            using (var well = new SolidBrush(Color.FromArgb(170, 8, 6, 4))) g.FillRectangle(well, box);
            using (var rim = new Pen(Theme.Bronze, Math.Max(1, s))) g.DrawRectangle(rim, box.X, box.Y, box.Width, box.Height);
            using var gold = new SolidBrush(Theme.Gold);
            g.DrawString(glyph, symbolFont, gold, box, center);
        }

        // Each limit: its line across the full width, then the bar with the percentage beside it, like a unit's hit points.
        for (int i = 0; i < rows.Count; i++)
        {
            float y = (Top0 + 28 + 44 * i) * s;
            Write(Fmt.Detail(rows[i].Label, rows[i].Limit, rows[i].Window), textFont, RowColor(i), pad, y);
            DrawBar(BarBox(i), rows[i].Limit);
            DrawPercent(rows[i].Limit, y + 17 * s);
        }

        var footer = DateTime.Now < opacityHintUntil ? $"opacity {Opacity:P0}"
            : error ?? (usage == null ? "No data yet: send a message in Claude Code" : $"updated {Fmt.Ago(usage.UpdatedAt)}");
        Write(footer, smallFont, Theme.Muted, pad, (Top0 + 23 + 44 * rows.Count) * s);
    }

    // Rectangle with corners of diameter d.
    static GraphicsPath Rounded(RectangleF r, float d)
    {
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // Pixels the frame art leaves (mostly) transparent become the colour key, so the desktop shows there instead of black;
    // the rest are made opaque over dark wood, so soft edges don't fringe pink.
    static void KeyOutTransparency(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var px = new int[bmp.Width * bmp.Height]; // 32-bit rows have no padding
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        int key = SeeThrough.ToArgb(), bg = Theme.Bg.ToArgb();
        for (int i = 0; i < px.Length; i++)
        {
            int a = px[i] >>> 24, c = px[i];
            if (a < 128) { px[i] = key; continue; }
            if (a == 255) continue;
            int Mix(int shift) => (((c >> shift) & 255) * a + ((bg >> shift) & 255) * (255 - a)) / 255;
            px[i] = unchecked((int)0xFF000000) | Mix(16) << 16 | Mix(8) << 8 | Mix(0);
        }
        Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        tray.Visible = false; // otherwise a dead icon lingers in the tray until hovered
        tray.Dispose();
        pulseTimer.Dispose();
        jobsWatcher?.Dispose();
        chatTip.Dispose();
        frameCache?.Dispose();
        Sound.Close();
        if (trayIconHandle != 0) Native.DestroyIcon(trayIconHandle);
        base.OnFormClosed(e);
    }
}

// The "+" box: what Claude should do, and in which folder. Enter starts it in the background, Esc cancels.
sealed class NewChatForm : Form
{
    readonly TextBox task = new()
    {
        Multiline = true, WordWrap = true, Bounds = new(16, 16, 356, 80), BorderStyle = BorderStyle.FixedSingle,
        BackColor = Theme.Panel, ForeColor = Theme.Fg, PlaceholderText = "What should Claude do?",
    };
    readonly ComboBox folder = new() { Bounds = new(16, 104, 314, 24), FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Fg };
    public string Prompt => task.Text.Trim();
    public string Folder => folder.Text.Trim();

    public NewChatForm(List<string> folders)
    {
        AutoScaleDimensions = new SizeF(96, 96); // laid out at 96 DPI, scaled to the monitor
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "New Claude chat — what should it do?  Enter starts"; // the box is focused, so its placeholder never shows
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        Cursor = Theme.Warcraft ? Art.Cursor : Cursors.Default;
        ClientSize = new Size(388, 178);
        DoubleBuffered = true;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        Font = new Font("Segoe UI", 9f);
        folder.Items.AddRange([.. folders]);
        folder.Text = folders.FirstOrDefault() ?? "";

        static Button Flat(string text, Rectangle bounds, Color back) =>
            new() { Text = text, Bounds = bounds, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Theme.Fg, FlatAppearance = { BorderColor = Theme.Track } };
        var browse = Flat("…", new(336, 103, 36, 26), Theme.Panel);
        browse.FlatAppearance.BorderColor = Theme.Bronze;
        var start = Flat("Start", new(276, 138, 96, 28), Theme.Warcraft ? Theme.Bg : Theme.Level(75));
        if (Theme.Warcraft) // the warcraftcn button: its own image, no border, gold Cinzel lettering
        {
            (start.BackgroundImage, start.BackgroundImageLayout, start.ForeColor) = (Art.Button, ImageLayout.Stretch, Theme.Gold);
            start.FlatAppearance.BorderSize = 0;
            start.Font = new Font(Art.Serif, 10f, FontStyle.Bold);
        }
        browse.Click += (_, _) =>
        {
            using var pick = new FolderBrowserDialog { Description = "Folder for the new chat", UseDescriptionForTitle = true, InitialDirectory = Folder };
            if (pick.ShowDialog(this) == DialogResult.OK) folder.Text = pick.SelectedPath;
        };
        start.Click += (_, _) =>
        {
            if (Prompt.Length == 0) { task.Focus(); return; }
            if (!Directory.Exists(Folder)) { folder.BackColor = Color.FromArgb(96, 44, 44); folder.Focus(); return; } // red tint: no such folder
            DialogResult = DialogResult.OK;
        };
        folder.TextChanged += (_, _) => folder.BackColor = Theme.Panel;
        AcceptButton = start; // Enter in the task box (Ctrl+Enter still makes a new line)
        Controls.AddRange([task, folder, browse, start]);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessDialogKey(keyData);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1; // DWMWA_USE_IMMERSIVE_DARK_MODE: dark title bar to match the card
        Native.DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }

    protected override void OnPaint(PaintEventArgs e) // the dark wood frame behind the fields
    {
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        if (Theme.Warcraft) Art.Frame(e.Graphics, Art.Panel, ClientRectangle, Art.PanelEdge, 0.16f * DeviceDpi / 96f, Theme.Bg);
        else base.OnPaint(e);
    }
}

// A click-through, see-through window that shoots a small confetti burst from a point, then closes itself.
sealed class Confetti : Form
{
    sealed class Piece { public float X, Y, Vx, Vy, Angle, Spin, Size; public required Color Color; public bool Round; }

    const int Frames = 100; // ~1.6 s at 60 fps
    readonly List<Piece> pieces = [];
    readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    readonly float scale;
    int frame;

    // gore: the click-spam burst, blood and chunks flying every way instead of a party fan.
    public static void Pop(Rectangle spawn, float scale, bool gore = false) => new Confetti(spawn, scale, gore).Show();

    Confetti(Rectangle spawn, float scale, bool gore)
    {
        this.scale = scale;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = TransparencyKey = Color.Magenta; // magenta pixels become see-through
        // Room around the spawn area for pieces to fly up and flutter down.
        float room = gore ? 1.8f : 1;
        Bounds = Rectangle.FromLTRB(spawn.Left - (int)(120 * room * scale), spawn.Top - (int)(220 * room * scale),
            spawn.Right + (int)(120 * room * scale), spawn.Bottom + (int)(100 * room * scale));
        float left = spawn.Left - Left, top = spawn.Top - Top;

        var rnd = Random.Shared;
        Color[] colors = gore
            ? [Color.FromArgb(170, 10, 10), Color.FromArgb(200, 20, 16), Color.FromArgb(232, 38, 30), Color.FromArgb(140, 6, 6), Color.FromArgb(150, 52, 40)]
            : [Theme.Level(0), Theme.Level(50), Theme.Level(75), Theme.Level(100), Theme.Fg, Color.FromArgb(150, 180, 240)];
        for (int i = 0; i < (gore ? 170 : 90); i++)
        {
            double angle = -Math.PI / 2 + (rnd.NextDouble() - 0.5) * (gore ? 5.0 : 1.8); // gore: nearly all round; confetti: an upward fan, about ±50°
            float speed = (gore ? 5 + rnd.NextSingle() * 12 : 4 + rnd.NextSingle() * 7) * scale;
            pieces.Add(new Piece
            {
                X = left + rnd.NextSingle() * spawn.Width, Y = top + rnd.NextSingle() * spawn.Height,
                Vx = (float)Math.Cos(angle) * speed, Vy = (float)Math.Sin(angle) * speed,
                Angle = rnd.NextSingle() * 360, Spin = (rnd.NextSingle() - 0.5f) * 24,
                Size = (gore ? 2 + rnd.NextSingle() * 6 : 2.5f + rnd.NextSingle() * 3) * scale, Color = colors[rnd.Next(colors.Length)],
                Round = gore ? rnd.Next(4) != 0 : rnd.Next(4) == 0, // gore: mostly droplets, some chunks
            });
        }
        timer.Tick += (_, _) => { if (++frame > Frames) Close(); else { Step(); Invalidate(); } };
        timer.Start();
    }

    void Step()
    {
        foreach (var p in pieces)
        {
            p.Vy += 0.3f * scale; // gravity
            p.Vx *= 0.97f; p.Vy *= 0.97f; // air drag, so pieces flutter down
            p.X += p.Vx; p.Y += p.Vy;
            p.Angle += p.Spin;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        // WS_EX_TRANSPARENT (clicks pass through) | WS_EX_TOOLWINDOW (no Alt+Tab) | WS_EX_NOACTIVATE
        get { var cp = base.CreateParams; cp.ExStyle |= 0x20 | 0x80 | 0x08000000; return cp; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; // no anti-aliasing: blended edge pixels would show the magenta key as a pink fringe
        float shrink = Math.Min(1f, (Frames - frame) / 25f); // pieces shrink away at the end instead of fading
        foreach (var p in pieces)
        {
            using var brush = new SolidBrush(p.Color);
            float s = p.Size * shrink;
            if (p.Round) { g.FillEllipse(brush, p.X - s / 2, p.Y - s / 2, s, s); continue; }
            double a = p.Angle * Math.PI / 180;
            float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a), hw = s, hh = s / 2; // 2:1 paper strip
            PointF Corner(float dx, float dy) => new(p.X + dx * cos - dy * sin, p.Y + dx * sin + dy * cos);
            g.FillPolygon(brush, [Corner(-hw, -hh), Corner(hw, -hh), Corner(hw, hh), Corner(-hw, hh)]);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Dispose();
        base.OnFormClosed(e);
    }
}

static class Native
{
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(nint hIcon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadCursorFromFileW")] public static extern nint LoadCursorFromFile(string fileName);
    [DllImport("userenv.dll", SetLastError = true)] public static extern bool CreateEnvironmentBlock(out nint environment, nint token, bool inherit);
    [DllImport("userenv.dll")] public static extern bool DestroyEnvironmentBlock(nint environment);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern int mciSendString(string command, StringBuilder? ret, int retLength, nint callback);
}

static class SelfTest
{
    public static int Run()
    {
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        var failures = new List<string>();
        void Check(bool ok, string name) { if (!ok) failures.Add(name); }

        var u = Usage.Parse($$$"""
            {"rate_limits":{"five_hour":{"used_percentage":23.5,"resets_at":{{{now + 3 * 3600 + 150}}}},
             "seven_day":{"used_percentage":141,"resets_at":{{{now - 10}}}}},"updated_at":{{{now}}}}
            """)!;
        Check(u.Session is { Used: 23.5 }, "parses session %");
        Check(u.Week is { Used: 100 }, "clamps to 100%");
        Check(u.Week!.UsedNow == 0 && u.Week.HasReset, "passed reset counts as 0%");
        Check(Fmt.Until(u.Session!.ResetsAt) == "3h 2m", "countdown");
        Check(Usage.Parse("""{"rate_limits":{"five_hour":{"used_percentage":"9"}}}""")!.Session == null, "ignores bad fields");
        Check(Usage.Parse("""{"other":1}""") == null && Usage.Parse("[]") == null, "no rate_limits = no data");
        Check(Fmt.Tooltip(u, null).Length <= 127 && Fmt.Tooltip(null, new string('x', 300)).Length == 127, "tooltip fits");
        Check(Theme.Level(0) == Color.FromArgb(52, 186, 72) && Theme.Level(100) == Color.FromArgb(214, 46, 38)
            && Theme.Level(-5) == Theme.Level(0) && Theme.Level(50) == Color.FromArgb(236, 196, 48), "colour stops");
        Check(Theme.Level(62.5) == Color.FromArgb(233, 157, 41), "colour blends between stops");
        Theme.Warcraft = false;
        Check(Theme.Level(0) == Color.FromArgb(108, 196, 161) && Theme.Level(62.5) == Color.FromArgb(224, 156, 88)
            && Theme.Bg == Color.FromArgb(31, 30, 29) && !Art.Covers("plain"), "the plain look keeps its own palette and font");
        Theme.Warcraft = true;
        Check(WidgetForm.JustReset(false, true) && !WidgetForm.JustReset(null, true) && !WidgetForm.JustReset(true, true)
            && !WidgetForm.JustReset(false, null), "confetti only on a live reset");
        Check(Fmt.Percent(u.Session, false) == "24%" && Fmt.Percent(u.Session, true) == "77% left"
            && Fmt.Percent(u.Week, true) == "100% left" && Fmt.Percent(null, true) == "–", "used / remaining text");
        Check(File.Exists(Sound.FilePath), "confetti.mp3 ships next to the exe");

        var parsed = Chats.Parse("""
            [{"id":"aa11","kind":"background","name":"fix bug","state":"working","cwd":"D:\\x"},
             {"id":"bb22","kind":"background","name":"ask","state":"blocked"},
             {"id":"cc33","kind":"background","name":"old","state":"done"},
             {"id":"x; rm","kind":"background","state":"working"},
             {"pid":1,"kind":"interactive","id":"dd44","state":"working"}, 7]
            """);
        Check(parsed.Select(c => c.Id).SequenceEqual(["aa11", "bb22", "cc33"]) && parsed[0] is { Name: "fix bug", Cwd: @"D:\x" }, "parses background chats only");
        var pasted = Chats.Parse($$"""
            [{"id":"ee55","kind":"background","name":"first line\n{{new string('x', 50000)}}","state":"failed"},
             {"id":"ff66","kind":"background","name":"{{new string('y', 300)}}","state":"working"}]
            """);
        Check(pasted[0].Name == "first line" && pasted[1].Name.Length == 121, "a pasted-log name is cut to one short line");
        HashSet<string> active = [], finished = [];
        Check(Chats.Visible(parsed, active, finished).Select(c => c.Id).SequenceEqual(["bb22", "aa11"]), "hides chats that were already done, needs-input first");
        var later = parsed.Select(c => c.Id == "aa11" ? c with { State = "done" } : c).ToList();
        Check(Chats.Visible(later, active, finished).Select(c => c.Id).SequenceEqual(["bb22", "aa11"]) && finished.Contains("aa11"), "lists a chat that just finished");
        finished.Remove("aa11");
        Check(Chats.Visible(later, active, finished).Select(c => c.Id).SequenceEqual(["bb22"]), "an opened done chat leaves the list");
        Check(Chats.Status("blocked").Text == "needs input" && Chats.Status("whatever").Text == "whatever", "status labels");
        var t0 = DateTimeOffset.Now;
        var pace = new Limit(60, t0.AddHours(3)); // 2 h into a 5 h window at 30 %/h: full in 80 min
        Check(pace.FullAt(TimeSpan.FromHours(5)) is { } fullAt && Math.Abs((fullAt - t0).TotalMinutes - 80) < 1, "forecasts when a limit fills");
        Check(new Limit(50, t0.AddHours(2.5)).FullAt(TimeSpan.FromHours(5)) == null
            && new Limit(10, t0.AddHours(4.9)).FullAt(TimeSpan.FromHours(5)) == null, "no forecast when it lasts, or too early to tell");
        Check(Fmt.Detail("5-hour", pace, TimeSpan.FromHours(5)).Contains("full ~"), "forecast shows on the card");
        Check(WidgetForm.Crossed(79, 81) && WidgetForm.Crossed(70, 96) && !WidgetForm.Crossed(81, 90) && !WidgetForm.Crossed(96, 99), "warns once per threshold");
        Check(Art.Card != null && Art.Panel != null && Art.Button != null && Art.Bar != null && Art.Serif.Name == "Cinzel"
            && Art.Cursor != Cursors.Default, "Warcraft art, font and cursor ship in art\\");
        Check(Art.Covers("BIG NEW TUNNEL LOGIC · café…") && !Art.Covers("Привет"), "Cinzel only for text it can spell");
        Check(Sound.VoiceFiles.All(File.Exists), "peon voice lines ship in sounds\\");
        var (su, sc, sid, sline) = StatusLine.Parse("""{"session_id":"abc-1","model":{"display_name":"Opus 5.5"},"context_window":{"used_percentage":41.5},"rate_limits":{"five_hour":{"used_percentage":15,"resets_at":1},"seven_day":{"used_percentage":35.4,"resets_at":2}}}""");
        Check(sline == "Opus 5.5 · ctx 42% · 5h 15% · 7d 35%" && su?["rate_limits"] != null && sc?["used_percentage"]?.GetValue<double>() == 41.5 && sid == "abc-1",
            "the built-in status line does statusline.js's job");
        Check(StatusLine.Parse("""{"session_id":"..\x","context_window":{"used_percentage":5}}""").Context == null && StatusLine.Parse("junk").Line == "",
            "the status line ignores unsafe session ids and junk");
        var (merged, replaced) = Installer.WithStatusLine("""{"model":"opus", "statusLine":{"type":"command","command":"node x.js"},}""", "\"C:/w/a.exe\" --statusline");
        var mergedRoot = JsonNode.Parse(merged)!;
        Check(replaced == "node x.js" && mergedRoot["model"]!.ToString() == "opus" && mergedRoot["statusLine"]!["command"]!.ToString() == "\"C:/w/a.exe\" --statusline",
            "the installer keeps every other setting and says what it replaces");
        Check(Installer.WithStatusLine(null, "x").Replaced == null && Installer.WithStatusLine("""{"statusLine":{"command":"x"}}""", "x").Replaced == null,
            "a fresh or already-hooked setup replaces nothing");
        var payload = typeof(Installer).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("payload/")).ToList();
        Check(payload.Count == 11 && payload.Any(n => n.EndsWith("card.png")) && payload.Any(n => n.EndsWith("explode.wav")) && !payload.Any(n => n.Contains("Peon") || n.Contains("Grunt")),
            "the setup carries the art and our own sounds, and no Blizzard audio");
        Check(Sound.PackCoversLines, "every orc line can be fetched (and hash-checked), and nothing unused is");
        Dictionary<string, string> seen = [];
        Chats.Chat C(string id, string state) => new(id, id, state, "");
        Check(WidgetForm.VoiceForChanges([C("a", "working"), C("b", "working")], seen) == null, "first sight is quiet");
        Check(WidgetForm.VoiceForChanges([C("a", "done"), C("b", "working")], seen) == null, "a finished chat is quiet");
        Check(WidgetForm.VoiceForChanges([C("a", "failed"), C("b", "blocked")], seen) == Sound.Voice.NeedsInput, "needing you beats failing");
        Check(WidgetForm.VoiceForChanges([C("a", "failed"), C("b", "blocked")], seen) == null, "no change, no line");
        Check(parsed[0].SessionId == "" && Chats.ReadContext(parsed[0] with { SessionId = @"..\..\x" }) == null, "context file name must be a plain id");
        Check(Chats.Tooltip(parsed[0] with { Context = 85 }).Contains("context 85% (nearly full") && Chats.Tooltip(parsed[1]).Contains("no data yet"), "chat popup text");

        var report = failures.Count == 0 ? "selftest ok\n" : $"selftest FAILED: {string.Join(", ", failures)}\n";
        using var stdout = Console.OpenStandardOutput(); // WinExe has no console; this still works when piped
        stdout.Write(Encoding.UTF8.GetBytes(report));
        return failures.Count == 0 ? 0 : 1;
    }
}
