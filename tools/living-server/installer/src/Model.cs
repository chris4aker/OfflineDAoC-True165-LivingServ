using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace LivingServerSetup
{
    /// <summary>The Living Server package and the game's own downloader embedded in this exe.</summary>
    internal static class Payload
    {
        public static Dictionary<string, string> BuildInfo = new Dictionary<string, string>();
        public static string BuildName => Get("BuildName");
        /// <summary>
        /// The newest True 1.65 release the package fits, e.g. 0.7: the one Setup downloads and updates to. BUILD-INFO may
        /// list several ("0.7,0.8") once a build fits a newer release too; a game on any listed release takes it as it is.
        /// </summary>
        public static string BaseRelease => NewestRelease(Get("BaseRelease"));
        /// <summary>Every release the package fits.</summary>
        public static IReadOnlyList<string> BaseReleases => Releases(Get("BaseRelease"));
        public static bool Fits(string release) => release != null && BaseReleases.Contains(release);
        public static string Commit => Get("Commit");
        /// <summary>"0.7" or "0.7 / 0.8": for labels that name every release the package fits.</summary>
        public static string FitsLabel => string.Join(" / ", BaseReleases.OrderBy(r => new Version(r)));
        public static string ShortCommit => Commit.Length > 7 ? Commit.Substring(0, 7) : Commit;

        public static List<string> Releases(string list) =>
            (list ?? "").Split(',').Select(r => r.Trim()).Where(r => Regex.IsMatch(r, @"^\d+(?:\.\d+)+$")).Distinct().ToList();

        public static string NewestRelease(string list)
        {
            var all = Releases(list);
            return all.Count == 0 ? "" : all.OrderBy(r => new Version(r)).Last();
        }
        public static string ServerDll => Get("server/GameServer.dll").ToLowerInvariant();
        /// <summary>The released True 1.65 server this package replaces (BUILD-INFO.txt base/runtime/server/GameServer.dll).</summary>
        public static string ReleasedServerDll => Get("base/runtime/server/GameServer.dll").ToLowerInvariant();
        public static string RootFolder;
        static byte[] zipBytes;

        static string Get(string key) => BuildInfo.TryGetValue(key, out var v) ? v : "";

        /// <summary>"1.0-beta" reads as "Beta 1.0"; anything else as it is.</summary>
        public static string DisplayName
        {
            get
            {
                var m = Regex.Match(BuildName, @"^(\d+(?:\.\d+)*)-beta(?:\.(\d+))?$");
                if (m.Success) return "Beta " + m.Groups[1].Value + (m.Groups[2].Success ? " (build " + m.Groups[2].Value + ")" : "");
                return BuildName;
            }
        }

        /// <summary>The package embedded in this exe (Beta 1.1.2: kept when a newer one from GitHub is used instead).</summary>
        public static string EmbeddedBuildName { get; private set; } = "";

        public static void Load()
        {
            using (var s = Theme.Resource("LivingServer.zip"))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                Use(ms.ToArray(), "embedded");
            }
            EmbeddedBuildName = BuildName;
        }

        /// <summary>
        /// Makes this package the one Setup installs: the embedded one, or a newer one PackageUpdate downloaded and checked.
        /// Everything is read first and swapped in only when complete, so a bad package leaves the current one in place.
        /// </summary>
        public static void Use(byte[] bytes, string what)
        {
            var info = new Dictionary<string, string>();
            string root;
            using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.Count(c => c == '/') == 1 && e.Name == "BUILD-INFO.txt")
                    ?? throw new InvalidDataException("The " + what + " Living Server package has no BUILD-INFO.txt.");
                root = entry.FullName.Substring(0, entry.FullName.IndexOf('/'));
                using (var r = new StreamReader(entry.Open()))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        var m = Regex.Match(line, @"^\s*([^=#]+?)\s*=\s*(.*?)\s*$");
                        if (m.Success) info[m.Groups[1].Value] = m.Groups[2].Value;
                    }
                }
            }
            string Value(string key) => info.TryGetValue(key, out var v) ? v : "";
            if (string.IsNullOrEmpty(Value("BuildName")) || string.IsNullOrEmpty(NewestRelease(Value("BaseRelease"))) || string.IsNullOrEmpty(Value("server/GameServer.dll")))
                throw new InvalidDataException("The " + what + " Living Server package's BUILD-INFO.txt is incomplete.");
            BuildInfo = info;
            RootFolder = root;
            zipBytes = bytes;
        }

        /// <summary>Unpacks the package into &lt;game&gt;\&lt;RootFolder&gt; (as a player would by hand) and returns that folder.
        /// Every file is written fresh and compared byte for byte; the package's own scripts then check BUILD-INFO.</summary>
        public static string ExtractPackage(string game)
        {
            var target = Path.Combine(game, RootFolder);
            using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
            {
                var prefix = RootFolder + "/";
                var full = Path.GetFullPath(target).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                foreach (var e in zip.Entries)
                {
                    if (!e.FullName.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected entry in the Living Server package: " + e.FullName);
                    var rel = e.FullName.Substring(prefix.Length);
                    if (rel.Length == 0) continue;
                    var dest = Path.GetFullPath(Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(full, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe entry in the Living Server package: " + e.FullName);
                    if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    byte[] data;
                    using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }
                    if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
                    File.WriteAllBytes(dest, data);
                    if (!File.ReadAllBytes(dest).SequenceEqual(data)) throw new IOException("Writing " + dest + " failed verification.");
                }
            }
            return target;
        }

        /// <summary>Writes the game's own downloader (release/Get-OfflineDAoC-True165.ps1) into dir (its downloads cache, .downloads, goes beside it).</summary>
        public static string ExtractDownloader(string dir)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "Get-OfflineDAoC-True165.ps1");
            byte[] data;
            using (var s = Theme.Resource("Get-OfflineDAoC-True165.ps1")) using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }
            if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(data)) File.WriteAllBytes(path, data);
            return path;
        }
    }

    internal static class Hash
    {
        public static string File(string path)
        {
            using (var sha = SHA256.Create())
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }
    }

    internal enum LsState { None, ThisBuild, OtherBuild, Modified, Unknown }

    /// <summary>An Offline DAoC True 1.65 game folder as found on disk.</summary>
    internal sealed class GameInfo
    {
        public string Path;
        public string Release;          // e.g. "0.7", from VERSION.txt ("Offline DAoC True 1.65 v0.7")
        public bool Unfinished;         // the launcher or the client is missing: a download that didn't finish
        public LsState Ls = LsState.Unknown;
        public string LsBuild;          // the Living Server build installed, if any

        // True 1.65 has one edition and always its new client; kept so the pages read the same.
        public bool Classic => false;
        public bool NewClient => true;
        public bool ClientBehind => false;
        public string Edition => Release == null ? "unknown version" : "v" + Release;
        public string Title => "Offline DAoC True 1.65 " + Edition;

        public int CompareToBase()
        {
            if (Release == null) return -2;
            return new Version(Release).CompareTo(new Version(Payload.BaseRelease));
        }

        public bool IsBase => Payload.Fits(Release);
        /// <summary>The game's own update brings it to the release Living Server fits (its patch lists the versions it starts from).</summary>
        public bool IsUpdatable => Release != null && Manifests.UpdatableFrom.Contains(Release);
        /// <summary>Living Server fits it as it is.</summary>
        public bool Ready => IsBase && !Unfinished;
    }

    internal static class GameScanner
    {
        public const string StartFile = "START OFFLINE DAOC TRUE 1.65.cmd";

        public static bool IsGame(string dir)
        {
            try
            {
                return File.Exists(System.IO.Path.Combine(dir, StartFile)) &&
                       File.Exists(System.IO.Path.Combine(dir, "runtime", "server", "GameServer.dll"));
            }
            catch { return false; }
        }

        /// <summary>Looks where people put the game: beside this exe, C:\Games, Downloads, Desktop, Documents and the
        /// top of each fixed drive, a few folders deep, skipping system folders.</summary>
        public static List<string> FindGames(string exeDir)
        {
            var roots = new List<string>();
            void Add(string p) { try { if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) roots.Add(System.IO.Path.GetFullPath(p)); } catch { } }
            Add(exeDir);
            Add(System.IO.Path.GetDirectoryName(exeDir));
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Add(System.IO.Path.Combine(home, "Downloads"));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            Add(System.IO.Path.Combine(home, "Games"));
            foreach (var d in SafeDrives())
            {
                Add(System.IO.Path.Combine(d, "Games"));
                Add(d);
            }
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int budget = 6000;
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool driveRoot = System.IO.Path.GetPathRoot(root).Equals(root, StringComparison.OrdinalIgnoreCase);
                Walk(root, driveRoot ? 2 : 3, found, seen, ref budget, driveRoot);
            }
            return found;
        }

        static IEnumerable<string> SafeDrives()
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                bool ok = false;
                try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { }
                if (ok) yield return d.RootDirectory.FullName;
            }
        }

        static readonly string[] Skip = { "windows", "program files", "program files (x86)", "programdata", "$recycle.bin",
            "system volume information", "appdata", "node_modules", ".git", "runtime", "new-client", "source", "msocache",
            "recovery", "perflogs", ".downloads" };

        static void Walk(string dir, int depth, List<string> found, HashSet<string> seen, ref int budget, bool onlyNamed)
        {
            if (budget-- <= 0 || !seen.Add(dir)) return;
            if (IsGame(dir)) { found.Add(dir); return; }
            if (depth <= 0) return;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { return; }
            foreach (var s in subs)
            {
                var name = System.IO.Path.GetFileName(s).ToLowerInvariant();
                if (Skip.Contains(name) || name.StartsWith(".")) continue;
                // at a drive's root, only folders that look like the game's
                if (onlyNamed && !(name.Contains("daoc") || name.Contains("true165") || name.Contains("true 1.65") || name == "games")) continue;
                try { if ((new DirectoryInfo(s).Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) continue; } catch { continue; }
                Walk(s, depth - 1, found, seen, ref budget, false);
            }
        }

        /// <summary>"0.7" from VERSION.txt, or null.</summary>
        public static string ReadRelease(string game)
        {
            try
            {
                var file = System.IO.Path.Combine(game, "VERSION.txt");
                if (!File.Exists(file)) return null;
                var m = Regex.Match(File.ReadAllText(file), @"v(\d+(?:\.\d+)+)");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        public static GameInfo Inspect(string dir)
        {
            var g = new GameInfo { Path = System.IO.Path.GetFullPath(dir).TrimEnd('\\', '/') };
            g.Release = ReadRelease(g.Path);
            g.Unfinished = !File.Exists(System.IO.Path.Combine(g.Path, "runtime", "OfflineDAoC.exe")) ||
                           !Directory.Exists(System.IO.Path.Combine(g.Path, "new-client"));
            try { InspectLivingServer(g); } catch { g.Ls = LsState.Unknown; }
            return g;
        }

        static void InspectLivingServer(GameInfo g)
        {
            var hash = Hash.File(System.IO.Path.Combine(g.Path, "runtime", "server", "GameServer.dll"));
            if (hash == Payload.ServerDll) { g.Ls = LsState.ThisBuild; g.LsBuild = Payload.BuildName; return; }
            foreach (var rec in InstallRecords(g.Path))
            {
                if (!rec.TryGetValue("Files", out var files) || !(files is System.Collections.ArrayList list)) continue;
                foreach (var f in list.OfType<Dictionary<string, object>>())
                {
                    var game = f.TryGetValue("Game", out var gp) ? gp as string : null;
                    var installed = f.TryGetValue("Installed", out var ins) ? (ins as string)?.ToLowerInvariant() : null;
                    if (game == "runtime/server/GameServer.dll" && installed == hash)
                    {
                        g.Ls = LsState.OtherBuild;
                        g.LsBuild = rec.TryGetValue("Build", out var bn) ? bn as string : "an earlier build";
                        return;
                    }
                }
            }
            // On the release this build fits, the server must be the released one; on another release Setup can't tell
            // (its update checks every file itself), so it counts as none.
            if (g.IsBase) g.Ls = hash == Payload.ReleasedServerDll ? LsState.None : LsState.Modified;
            else g.Ls = LsState.None;
        }

        /// <summary>Living Server's install records, oldest first (rolled-back ones have another name and are left out).</summary>
        public static List<Dictionary<string, object>> InstallRecords(string game)
        {
            var records = new List<Dictionary<string, object>>();
            var root = System.IO.Path.Combine(game, "runtime", "server", "deployment-backups");
            if (!Directory.Exists(root)) return records;
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            foreach (var d in Directory.GetDirectories(root).OrderBy(x => x, StringComparer.Ordinal))
            {
                var f = System.IO.Path.Combine(d, "install-record.json");
                if (!File.Exists(f)) continue;
                try
                {
                    var rec = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(f).TrimStart('\uFEFF'));
                    if (rec != null) records.Add(rec);
                }
                catch { }
            }
            return records;
        }

        public static readonly string[] GameProcesses = { "CoreServer", "DOLServer", "OfflineDAoC", "Godot_v4.7.2-stable_mono_win64" };

        /// <summary>Names of Offline DAoC programs still running (the same list the package's scripts refuse on).</summary>
        public static List<string> RunningGameProcesses()
        {
            var names = new List<string>();
            foreach (var n in GameProcesses)
            {
                try { if (Process.GetProcessesByName(n).Length > 0) names.Add(n); } catch { }
            }
            return names;
        }

        /// <summary>Where each running Offline DAoC program was started from, so the message can name the game folder.</summary>
        public static List<string> RunningGameProcessDetails()
        {
            var details = new List<string>();
            foreach (var n in GameProcesses)
            {
                Process[] found;
                try { found = Process.GetProcessesByName(n); } catch { continue; }
                foreach (var p in found)
                {
                    string where = null;
                    try { where = System.IO.Path.GetDirectoryName(p.MainModule.FileName); } catch { }
                    bool window = false;
                    try { window = p.MainWindowHandle != IntPtr.Zero; } catch { }
                    details.Add(n + ".exe" + (where != null ? " in " + where : "") + (window ? "" : ", no window") + " (process " + p.Id + ")");
                    p.Dispose();
                }
            }
            return details;
        }
    }

    /// <summary>
    /// Sizes of the game's download and update, read from its release's download-manifest.json (with fallbacks from the
    /// v0.7 release notes, used only if GitHub can't be reached before the install starts).
    /// </summary>
    internal static class Manifests
    {
        public const string Repository = "zomboden/OfflineDAoC-True165";
        public const string Upstream = "https://github.com/" + Repository + "/releases/download/v";
        // v0.7's release notes: "One update covers every earlier version, v0.1 to v0.6". The live manifest's Patch.From wins.
        public static readonly HashSet<string> UpdatableFrom = new HashSet<string> { "0.1", "0.2", "0.3", "0.4", "0.4.1", "0.5", "0.6" };
        public static long Download = 36L * 1000 * 1000 * 1000;      // v0.7: the complete game, about 36 GB
        public static long Installed = 40L * 1000 * 1000 * 1000;
        public static long FreeSpace = 49L * 1000 * 1000 * 1000;     // v0.7: about 49 GB free for a new install
        public static long Patch = 3L * 1000 * 1000 * 1000;           // v0.7: the update in place, about 3.0 GB
        public static long PatchUnpacked = 3L * 1000 * 1000 * 1000;
        public static int Parts;
        public static readonly Dictionary<string, long> PartBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public static bool Live;

        /// <summary>Refreshes the sizes from the live manifest. Quiet on failure.</summary>
        public static void TryRefresh()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var wc = new QuickClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "OfflineDAoC-True165-LivingServer-Setup";
                    var text = Encoding.UTF8.GetString(wc.DownloadData(Upstream + Payload.BaseRelease + "/download-manifest.json"));
                    Apply(text);
                }
                Live = true;
            }
            catch (Exception e)
            {
                Log.Write("Could not read the release manifest (" + e.Message + "); using the v0.7 sizes.");
            }
        }

        /// <summary>Takes the sizes from a download-manifest.json (also the copy the downloader keeps in its cache).</summary>
        public static void Apply(string json)
        {
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var manifest = js.Deserialize<Dictionary<string, object>>(json.TrimStart('\uFEFF'));
            var parts = ((System.Collections.ArrayList)manifest["Parts"]).OfType<Dictionary<string, object>>().ToList();
            Download = parts.Sum(p => Convert.ToInt64(p["Bytes"]));
            Parts = parts.Count;
            foreach (var p in parts) PartBytes[(string)p["Name"]] = Convert.ToInt64(p["Bytes"]);
            if (manifest.TryGetValue("InstalledBytes", out var installed))
            {
                Installed = Convert.ToInt64(installed);
                FreeSpace = Installed + parts.Max(p => Convert.ToInt64(p["Bytes"])) + (1L << 30);   // the downloader's own check
            }
            if (manifest.TryGetValue("Patch", out var patchObj) && patchObj is Dictionary<string, object> patch)
            {
                if (patch.TryGetValue("Bytes", out var pb)) Patch = Convert.ToInt64(pb);
                if (patch.TryGetValue("UnpackedBytes", out var pu)) PatchUnpacked = Convert.ToInt64(pu);
                if (patch.TryGetValue("Parts", out var pp) && pp is System.Collections.ArrayList plist)
                    foreach (var p in plist.OfType<Dictionary<string, object>>()) PartBytes[(string)p["Name"]] = Convert.ToInt64(p["Bytes"]);
                if (patch.TryGetValue("From", out var from) && from is System.Collections.ArrayList list)
                {
                    UpdatableFrom.Clear();
                    foreach (var v in list.OfType<string>()) UpdatableFrom.Add(v);
                }
            }
        }
    }

    /// <summary>A WebClient that gives up after 10 seconds, so an offline PC isn't kept waiting.</summary>
    internal sealed class QuickClient : WebClient
    {
        protected override WebRequest GetWebRequest(Uri address)
        {
            var r = base.GetWebRequest(address);
            r.Timeout = 10000;
            if (r is HttpWebRequest h) h.ReadWriteTimeout = 10000;
            return r;
        }
    }

    internal static class Log
    {
        static readonly object gate = new object();
        public static string FilePath;
        public static event Action<string> Written;

        public static void Write(string line)
        {
            var stamped = DateTime.Now.ToString("HH:mm:ss") + "  " + line;
            lock (gate)
            {
                try { if (FilePath != null) System.IO.File.AppendAllText(FilePath, stamped + Environment.NewLine, Encoding.UTF8); } catch { }
            }
            Written?.Invoke(line);
        }
    }
}
