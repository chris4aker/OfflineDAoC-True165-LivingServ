using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace LivingServerSetup
{
    internal sealed class InstallOptions
    {
        public bool NewInstall;
        public string Destination;      // new install: the new game folder
        public GameInfo Game;           // existing game
        public bool ForceLivingServer;  // replace server/launcher files the package's installer doesn't recognise
        public bool FreshCopyIfChanged; // the game's update refused (changed files): its downloader makes a fresh copy with the save
        public bool Remove;             // take Living Server off (back to the released server and launcher)
        public bool Shortcuts = true;
        /// <summary>Turn every Living Server feature on at the suggested settings (the launcher's Recommended), after installing.</summary>
        public bool RecommendedSettings;
        public string LocalAssets;      // tests: the downloader reads release files from here instead of GitHub

        /// <summary>Where the game's downloader and its download cache go: beside the game folder, so it's on the same drive.</summary>
        public string SetupFilesDir
        {
            get
            {
                var anchor = NewInstall ? Destination : Game.Path;
                var parent = Path.GetDirectoryName(Path.GetFullPath(anchor).TrimEnd('\\', '/')) ?? anchor;
                return Path.Combine(parent, "Offline DAoC True 1.65 setup files");
            }
        }

        /// <summary>The game's own downloader runs: a new install, or an older release to update first.</summary>
        public bool NeedsUpstream => NewInstall || (!Remove && Game != null && !Game.IsBase);
    }

    internal enum Outcome { Success, Failed, Cancelled }

    internal sealed class InstallResult
    {
        public Outcome Outcome;
        public string Message;
        public string Hint;
        public bool CanForce;
        public bool CanFreshCopy;
        public bool CanChangeFolder;   // the folder was the problem: offer to go back and pick another
        public string GamePath;
        public string CacheDir;
        public string Note;            // something the player should know on the done page
        public long CacheBytes;
        /// <summary>Every Living Server feature was turned on at the suggested settings.</summary>
        public bool SettingsApplied;
    }

    internal enum StepId { Package, Check, Release, Download, Update, LivingServer, Settings, Shortcuts, Remove }

    /// <summary>Runs an install plan step by step on a worker thread. The UI reads the public state on Changed.</summary>
    internal sealed class Engine
    {
        /// <summary>A download error that means the connection went away, not that something is wrong with the game. Pure.</summary>
        public static bool LooksLikeNetworkDrop(string message) =>
            message != null && (message.IndexOf("could not be resolved", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                message.IndexOf("connection was reset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                message.IndexOf("unable to connect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                message.IndexOf("connection was closed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                message.IndexOf("Check your internet connection", StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// A download error that means GitHub's download server was busy or briefly down (429, 500, 502, 503, 504), not that
        /// anything is wrong with the game or the connection. Pure.
        /// </summary>
        public static bool LooksLikeBusyServer(string message) => message != null && BusyServer.IsMatch(message);

        static readonly Regex BusyServer = new Regex(
            @"\((?:429|500|502|503|504)\)|returned (?:an )?error:?\s*\(?(?:429|500|502|503|504)\b|does not indicate success:\s*(?:429|500|502|503|504)\b" +
            @"|Service Unavailable|Server Unavailable|Bad Gateway|Gateway Time-?out|Too Many Requests|Internal Server Error",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>How long Setup waits before running the game's downloader again after a busy server, one entry per try
        /// (7.5 minutes in all). Finished parts are kept, so each try continues from the last finished part.</summary>
        public static readonly int[] BusyRetrySeconds = { 30, 60, 120, 240 };

        /// <summary>busy, network or other: what Setup makes of a download error (--classify-download-error). Pure.</summary>
        public static string ClassifyDownloadError(string message) =>
            LooksLikeBusyServer(message) ? "busy" : LooksLikeNetworkDrop(message) ? "network" : "other";

        /// <summary>The game folder's path the True 1.65 client keeps its graphics cache within (the downloader's $MaxGamePath).</summary>
        public const int MaxGamePath = 42;

        sealed class Step
        {
            public StepId Id;
            public StepItem Item;
            public double Weight;
            public double Fraction;
            public bool Measured;      // the fraction is real; otherwise the step bar is indeterminate
        }

        readonly InstallOptions o;
        readonly List<Step> steps = new List<Step>();
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        readonly object gate = new object();
        public List<StepItem> Items => steps.Select(s => s.Item).ToList();
        public event Action Changed;

        // state for the UI
        public double Overall { get; private set; }
        public double? StepFraction { get; private set; }
        public string Status { get; private set; } = "";
        public string Detail { get; private set; } = "";
        public bool CanCancel { get; private set; }
        public InstallResult Result { get; private set; }

        string gamePath;
        readonly string setupDir;
        DownloadWatch watch;
        volatile bool upstreamRunning;
        bool rolledBack;
        string pendingNote;

        public Engine(InstallOptions options)
        {
            o = options;
            gamePath = o.NewInstall ? Path.GetFullPath(o.Destination).TrimEnd('\\', '/') : o.Game.Path;
            setupDir = o.SetupFilesDir;
            Plan();
        }

        void Add(StepId id, string title, double weight, string detail = null) =>
            steps.Add(new Step { Id = id, Weight = Math.Max(weight, 1), Item = new StepItem { Title = title, Detail = detail } });

        const double GB = 1073741824.0;

        void Plan()
        {
            var ls = "Living Server " + Payload.DisplayName;
            if (PackageUpdate.Note != null && !o.Remove) Add(StepId.Package, PackageUpdate.StepTitle, 0.01 * GB, PackageUpdate.Note);
            Add(StepId.Check, "Check your PC", 0.05 * GB);
            if (o.Remove)
            {
                Add(StepId.Remove, "Put the released server and launcher back", 0.3 * GB, "from the backups Living Server made when it was installed");
                return;
            }
            if (o.NewInstall)
            {
                Add(StepId.Download, "Download and unpack Offline DAoC True 1.65 v" + Payload.BaseRelease, Manifests.Download * 1.4,
                    Theme.Gb(Manifests.Download) + " in " + (Manifests.Parts > 0 ? Manifests.Parts + " " : "") + "checked parts, with the game's own downloader");
            }
            else if (!o.Game.IsBase)
            {
                if (o.Game.Ls != LsState.None)
                    Add(StepId.Release, "Put the released server back for the update", 0.2 * GB, "the game's update only applies to its released files; your settings stay");
                Add(StepId.Update, "Update the game to v" + Payload.BaseRelease, Manifests.Patch * 1.5,
                    Theme.Gb(Manifests.Patch) + ", the game's own update: your account, characters and bots stay");
            }
            if (!o.NewInstall && o.Game.Ready && o.Game.Ls == LsState.ThisBuild)
                Add(StepId.LivingServer, "Check " + ls, 0.3 * GB, "every file compared with the package; anything missing is put back");
            else
                Add(StepId.LivingServer, "Install " + ls, 0.5 * GB, "server and launcher; every file backed up first, and your world database");
            if (o.RecommendedSettings)
                Add(StepId.Settings, "Turn on Living Server's suggested settings", 0.01 * GB,
                    "bot personalities, voices and server life; everything else as True 1.65 has it");
            if (o.Shortcuts) Add(StepId.Shortcuts, "Add shortcuts", 0.02 * GB, "on the desktop and in the Start menu");
        }

        Step Find(StepId id) => steps.FirstOrDefault(s => s.Id == id);

        /// <summary>The launcher's code holds the "--apply-recommended-settings" string (UTF-16, as .NET stores literals).</summary>
        static bool LauncherHasSettingsMode(string dll)
        {
            try
            {
                if (!File.Exists(dll)) return false;
                var needle = System.Text.Encoding.Unicode.GetBytes("--apply-recommended-settings");
                var data = File.ReadAllBytes(dll);
                for (int i = 0; i + needle.Length <= data.Length; i++)
                {
                    int k = 0;
                    while (k < needle.Length && data[i + k] == needle[k]) k++;
                    if (k == needle.Length) return true;
                }
            }
            catch (Exception e) { Log.Write("Recommended settings: " + e.Message); }
            return false;
        }

        /// <summary>
        /// Runs the launcher just installed from this package in its settings mode, which writes the suggested Living Server
        /// settings through the same save as its Living Server tab. Null when saved, else why not. Never fatal.
        /// </summary>
        static string ApplyRecommendedSettings(string game)
        {
            var launcher = Path.Combine(game, "runtime", "OfflineDAoC.exe");
            if (!File.Exists(launcher)) return "no launcher in runtime";
            if (!LauncherHasSettingsMode(Path.Combine(game, "runtime", "OfflineDAoC.dll")))
            {
                Log.Write("Recommended settings: this launcher has no settings mode");
                return "this launcher has no settings mode";
            }
            var dotnet = Path.Combine(game, "tools", "dotnet");
            var psi = new ProcessStartInfo(launcher, "--apply-recommended-settings")
            {
                WorkingDirectory = Path.GetDirectoryName(launcher), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            if (Directory.Exists(dotnet))
            {
                psi.EnvironmentVariables["DOTNET_ROOT"] = dotnet;
                psi.EnvironmentVariables["DOTNET_ROOT_X64"] = dotnet;
                psi.EnvironmentVariables["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            }
            try
            {
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEndAsync();
                    var error = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(60_000))
                    {
                        try { p.Kill(); } catch { }
                        return "the launcher didn't finish";
                    }
                    var text = (output.Result + " " + error.Result).Trim();
                    Log.Write("Recommended settings: exit " + p.ExitCode + (text.Length > 0 ? ": " + text : ""));
                    return p.ExitCode == 0 ? null : (text.Length > 0 ? text : "exit code " + p.ExitCode);
                }
            }
            catch (Exception e)
            {
                Log.Write("Recommended settings: " + e.Message);
                return e.Message;
            }
        }

        void Set(StepId id, StepState state, string detail = null)
        {
            var s = Find(id);
            if (s == null) return;
            lock (gate)
            {
                s.Item.State = state;
                if (detail != null) s.Item.Detail = detail;
                if (state == StepState.Done || state == StepState.Skipped) { s.Fraction = 1; }
            }
            Recompute();
        }

        void Activate(StepId id, string status, string detail = null)
        {
            var s = Find(id);
            if (s == null) return;
            lock (gate)
            {
                s.Item.State = StepState.Active;
                if (detail != null) s.Item.Detail = detail;
            }
            Status = status;
            Detail = "";   // the last step's numbers don't belong to this one
            Recompute();
        }

        void Progress(StepId id, double fraction, bool measured = true)
        {
            var s = Find(id);
            if (s == null) return;
            lock (gate) { s.Fraction = Math.Max(s.Fraction, Math.Min(1, fraction)); s.Measured = measured; }
            Recompute();
        }

        void Recompute()
        {
            lock (gate)
            {
                double total = steps.Sum(s => s.Weight);
                double done = steps.Sum(s => s.Weight * (s.Item.State == StepState.Done || s.Item.State == StepState.Skipped ? 1 : s.Fraction));
                Overall = total > 0 ? done / total : 0;
                var active = steps.FirstOrDefault(s => s.Item.State == StepState.Active);
                StepFraction = active != null && active.Measured ? active.Fraction : (double?)null;
                // Stopping is safe while a new install downloads and unpacks (each part is kept or marked done, and the
                // downloader continues an unfinished folder); not while an update applies to the player's game.
                CanCancel = upstreamRunning && active != null && active.Id == StepId.Download;
            }
            Changed?.Invoke();
        }

        void SetStatus(string status, string detail = null)
        {
            Status = status;
            if (detail != null) Detail = detail;
            Changed?.Invoke();
        }

        /// <summary>Screenshot mode: a believable moment part-way through a new install.</summary>
        public void DemoState()
        {
            foreach (var st in steps)
            {
                if (st.Id == StepId.Check) { st.Item.State = StepState.Done; st.Fraction = 1; }
                if (st.Id == StepId.Download) { st.Item.State = StepState.Active; st.Fraction = 0.42; st.Measured = true; }
            }
            Status = "Downloading part 8 of 19...";
            Detail = "14.1 GB of 33.6 GB  ·  48 MB/s  ·  about 7 min left";
            upstreamRunning = true;
            Recompute();
        }

        public void Cancel()
        {
            if (!CanCancel) return;
            Log.Write("Stop requested.");
            cts.Cancel();
        }

        /// <summary>Runs the whole plan. Call on a worker thread.</summary>
        public InstallResult Run()
        {
            var r = new InstallResult { GamePath = gamePath, CacheDir = Path.Combine(setupDir, ".downloads") };
            try
            {
                if (Find(StepId.Package) != null) Set(StepId.Package, PackageUpdate.FellBack ? StepState.Skipped : StepState.Done);
                Activate(StepId.Check, "Checking your PC...");
                CheckPc();
                Set(StepId.Check, StepState.Done);

                if (o.Remove)
                {
                    Activate(StepId.Remove, "Putting the released server and launcher back...");
                    int undone = RollBackAll(gamePath);
                    Set(StepId.Remove, StepState.Done, undone == 1 ? "one Living Server install undone" : undone + " Living Server installs undone, newest first");
                    r.Outcome = Outcome.Success;
                    return Result = r;
                }

                if (Find(StepId.Release) != null)
                {
                    Activate(StepId.Release, "Putting the released server back for the update...");
                    if ((o.Game.Ls == LsState.Modified || o.Game.Ls == LsState.Unknown) && !o.ForceLivingServer)
                        throw new StepFailed("This game's server or launcher files were changed by something other than Living Server, so Setup can't put the released ones back by itself.",
                            "The game's update then sees them as changed and offers a fresh copy with your save instead. Replace anyway goes ahead.") { CanForce = true };
                    if (GameScanner.InstallRecords(gamePath).Count > 0)
                    {
                        RollBackAll(gamePath);
                        rolledBack = true;
                    }
                    Set(StepId.Release, StepState.Done);
                }

                if (o.NeedsUpstream) RunUpstream(r);

                Activate(StepId.LivingServer, "Installing Living Server " + Payload.DisplayName + "...");
                var args = "-GameFolder " + ScriptJob.Quote(gamePath) + (o.ForceLivingServer ? " -Force" : "");
                RunPackageScript("install-server-build.ps1", args);
                rolledBack = false;
                Set(StepId.LivingServer, StepState.Done);

                if (Find(StepId.Settings) != null)
                {
                    Activate(StepId.Settings, "Turning on Living Server's suggested settings...");
                    string why = ApplyRecommendedSettings(gamePath);
                    r.SettingsApplied = why == null;
                    if (why == null) Set(StepId.Settings, StepState.Done, "on; change any of them in the launcher's Living Server tab");
                    else Set(StepId.Settings, StepState.Skipped, "not turned on (" + why + "): turn them on in the launcher's Living Server tab");
                }

                if (Find(StepId.Shortcuts) != null)
                {
                    Activate(StepId.Shortcuts, "Adding shortcuts...");
                    try { Shortcuts.Create(gamePath); Set(StepId.Shortcuts, StepState.Done); }
                    catch (Exception e) { Log.Write("Shortcuts: " + e.Message); Set(StepId.Shortcuts, StepState.Skipped, "couldn't add them: " + e.Message); }
                }

                r.Outcome = Outcome.Success;
                r.GamePath = gamePath;
                r.CacheBytes = DirSize(r.CacheDir);
                r.Note = pendingNote;
                Overall = 1;
                Status = "Done";
                Changed?.Invoke();
                return Result = r;
            }
            catch (StepFailed f)
            {
                r.Outcome = cts.IsCancellationRequested ? Outcome.Cancelled : Outcome.Failed;
                r.Message = cts.IsCancellationRequested ? "Stopped. Finished parts are kept, so running Setup again continues where it stopped." : f.Message;
                r.Hint = f.Hint;
                r.CanForce = f.CanForce;
                r.CanFreshCopy = f.CanFreshCopy;
                r.CanChangeFolder = f.CanChangeFolder;
                r.GamePath = gamePath;
                MarkFailed();
                RestoreAfterFailedUpdate(r);
                Log.Write((r.Outcome == Outcome.Cancelled ? "CANCELLED: " : "FAILED: ") + r.Message);
                return Result = r;
            }
            catch (Exception e)
            {
                r.Outcome = Outcome.Failed;
                r.Message = e.Message;
                r.GamePath = gamePath;
                MarkFailed();
                RestoreAfterFailedUpdate(r);
                Log.Write("FAILED: " + e);
                return Result = r;
            }
            finally
            {
                upstreamRunning = false;
                CanCancel = false;
                watch?.Dispose();
                Changed?.Invoke();
            }
        }

        void MarkFailed()
        {
            var a = steps.FirstOrDefault(s => s.Item.State == StepState.Active);
            if (a != null) { a.Item.State = cts.IsCancellationRequested ? StepState.Skipped : StepState.Failed; Recompute(); }
        }

        sealed class StepFailed : Exception
        {
            public string Hint; public bool CanForce; public bool CanFreshCopy; public bool CanChangeFolder;
            public StepFailed(string message, string hint = null) : base(message) { Hint = hint; }
        }

        // ---------------------------------------------------------------- checks

        void CheckPc()
        {
            var running = GameScanner.RunningGameProcesses();
            if (running.Count > 0)
            {
                var details = GameScanner.RunningGameProcessDetails();
                foreach (var d in details) Log.Write("Still running: " + d);
                bool hidden = details.Any(d => d.Contains(", no window"));
                throw new StepFailed("Offline DAoC is still running (" + string.Join(", ", running) + ").",
                    "Quit the game, click STOP SERVER in the launcher and close it, then click Try again." +
                    (hidden ? " One of them has no window: end it in Task Manager (Details: " +
                        string.Join(", ", details.Where(d => d.Contains(", no window"))) + ")." : ""));
            }

            if (o.NewInstall)
            {
                var dest = gamePath;
                var pf = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.Windows) };
                if (pf.Any(p => !string.IsNullOrEmpty(p) && dest.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                    throw new StepFailed("The game can't live under Program Files or Windows: it saves your world in its own folder.", "Choose a folder like C:\\Games\\OfflineDAoC-True165.") { CanChangeFolder = true };
                if (dest.IndexOf("OneDrive", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new StepFailed("That folder is inside OneDrive, which can move or lock the game's files while it runs.", "Choose a folder like C:\\Games\\OfflineDAoC-True165.") { CanChangeFolder = true };
                if (dest.Length > MaxGamePath)
                    throw new StepFailed("That folder's path is too long for the game (" + dest.Length + " characters; " + MaxGamePath + " at most, or the new client can't keep its graphics cache and every start is slow).",
                        "Choose a shorter folder, like C:\\Games\\OfflineDAoC-True165.") { CanChangeFolder = true };
                if (Regex.IsMatch(dest, @"[^\x20-\x7E]"))
                    throw new StepFailed("That folder's path has letters outside plain English, which some of the game's tools can't handle.", "Choose a folder like C:\\Games\\OfflineDAoC-True165.") { CanChangeFolder = true };
                // The downloader continues a folder it began (it has its .install marker); any other folder with files in
                // it is never installed over.
                if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any() && !ResumableNewInstall(dest))
                {
                    if (GameScanner.IsGame(dest))
                        throw new StepFailed("Offline DAoC True 1.65 " + GameScanner.Inspect(dest).Edition + " is already installed in " + dest + ".",
                            "Go back and choose that game on the first page to add Living Server to it, or choose a new folder for a second game.") { CanChangeFolder = true };
                    throw new StepFailed("The folder " + dest + " already has files in it, and they aren't an unfinished install of the game.", "Choose a new folder: Setup never installs over files it didn't put there.") { CanChangeFolder = true };
                }
            }
            var need = NeededBytes();
            var root = Path.GetPathRoot(o.NewInstall ? gamePath : setupDir);
            try
            {
                var free = new DriveInfo(root).AvailableFreeSpace;
                Log.Write("Free space on " + root + ": " + Theme.Gb(free) + "; needed about " + Theme.Gb(need));
                if (free < need)
                    throw new StepFailed("Not enough free disk space on " + root.TrimEnd('\\') + ": about " + Theme.Gb(need) + " is needed, " + Theme.Gb(free) + " is free.",
                        "Free up space or choose a folder on another drive, then try again.");
            }
            catch (StepFailed) { throw; }
            catch (Exception e) { Log.Write("Free space check skipped: " + e.Message); }
        }

        /// <summary>A new install the game's downloader began and didn't finish: it continues it part by part.</summary>
        public static bool ResumableNewInstall(string dest) => Directory.Exists(Path.Combine(dest, ".install"));

        public long NeededBytes()
        {
            if (o.Remove) return 64L << 20;
            long need = 512L << 20;
            if (o.NewInstall)
            {
                // the downloader unpacks each part as it arrives and deletes it, so the game plus one part is the peak
                need += ResumableNewInstall(gamePath) ? Math.Max(0, Manifests.Installed - DirSize(gamePath)) + (2L << 30) : Manifests.FreeSpace;
            }
            else if (!o.Game.IsBase)
            {
                need += o.FreshCopyIfChanged ? Manifests.FreeSpace : Manifests.Patch + Manifests.PatchUnpacked + (2L << 30);
            }
            return need;
        }

        // ---------------------------------------------------------------- the game's own downloader

        void RunUpstream(InstallResult r)
        {
            var script = Payload.ExtractDownloader(setupDir);
            string local = string.IsNullOrEmpty(o.LocalAssets) ? "" : " -LocalAssets " + ScriptJob.Quote(o.LocalAssets);
            string args = o.NewInstall
                ? "-Mode install -Yes -NoShortcut -ReleaseVersion " + Payload.BaseRelease + " -Destination " + ScriptJob.Quote(gamePath) + local
                : "-Mode update -Yes -NoShortcut -ReleaseVersion " + Payload.BaseRelease + " -Update " + ScriptJob.Quote(gamePath) + local;
            var step = o.NewInstall ? StepId.Download : StepId.Update;
            Activate(step, o.NewInstall ? "Getting the list of files..." : "Checking your game for the update...");
            if (o.FreshCopyIfChanged && !o.NewInstall) Find(StepId.Update).Item.Title = "Install a fresh copy and bring your save over";

            watch = new DownloadWatch(Path.Combine(setupDir, ".downloads", "v" + Payload.BaseRelease), gamePath, step, this);
            watch.Start();
            upstreamRunning = true;
            Recompute();
            string freshFolder = null;
            try
            {
                int busyTries = 0;
                while (true)
                {
                    var job = new ScriptJob();
                    bool changed = false, finished = false;
                    string lastOut = null;
                    job.Line += (line, err) =>
                    {
                        if (err) return;
                        lastOut = line.Trim();
                        OnUpstreamLine(line, step);
                        // The downloader's -Yes answers its "make a fresh install?" question itself: that is a 36 GB
                        // download, so Setup stops it unless the player chose it.
                        if (Regex.IsMatch(line, @"^\d+ file\(s\) this update would replace were changed on this PC"))
                        {
                            changed = true;
                            if (!o.FreshCopyIfChanged) job.Kill();
                        }
                        var m = Regex.Match(line, @"^A fresh .+ with your save is in (.+)$");
                        if (m.Success) freshFolder = m.Groups[1].Value.Trim();
                        if (line.StartsWith("To play:")) finished = true;
                    };
                    bool folderBefore = Directory.Exists(gamePath);
                    int code = job.Run(script, args, cts.Token);
                    if (cts.IsCancellationRequested) throw new StepFailed("Stopped.");
                    if (code == 0 && finished) break;
                    var msg = lastOut ?? job.LastError ?? "The game's downloader stopped with code " + code + ".";
                    if (changed && !o.FreshCopyIfChanged)
                        throw new StepFailed("Some of the game's own files were changed on this PC, so its update can't apply to this folder. Nothing was changed.",
                            "Setup can have the game's downloader make a fresh v" + Payload.BaseRelease + " copy next to it (" + Theme.Gb(Manifests.Download) +
                            ") and bring your save over: your account, characters, companions, bots and settings. Your old folder is only read.") { CanFreshCopy = true };
                    // GitHub's download server busy (429/5xx): wait and run the downloader again, as Try again would;
                    // finished parts are kept.
                    if (busyTries < BusyRetrySeconds.Length && LooksLikeBusyServer(msg) && (o.NewInstall || Directory.Exists(gamePath) == folderBefore))
                    {
                        int wait = BusyRetrySeconds[busyTries++];
                        Log.Write("GitHub's download server was busy (" + msg + "); trying again in " + wait + " s (" + busyTries + " of " + BusyRetrySeconds.Length + ").");
                        SetStatus("GitHub's download server is busy. Trying again in " + wait + " seconds (" + busyTries + " of " + BusyRetrySeconds.Length + ")...",
                            "Finished parts are kept; nothing needs doing.");
                        if (cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait))) throw new StepFailed("Stopped.");
                        continue;
                    }
                    throw new StepFailed(msg, LooksLikeBusyServer(msg)
                        ? "GitHub's download server is busy right now (Setup tried again " + busyTries + " times). Finished parts are kept: click Try again in a few minutes and it continues where it stopped."
                        : LooksLikeNetworkDrop(msg)
                        ? "The internet connection dropped. Finished parts are kept: when you're back online, click Try again and it continues where it stopped."
                        : "Finished parts are kept: click Try again and it continues where it stopped. The full output is in the log (Show details).");
                }
            }
            finally { watch.Stop(); upstreamRunning = false; Recompute(); }

            if (freshFolder != null)
            {
                Log.Write("The game now lives in " + freshFolder);
                // the old folder had its build taken off for the update: put it back there, and carry on in the new one
                if (rolledBack) { var note = RestorePrevious(gamePath); if (note != null) Log.Write(note); }
                rolledBack = false;
                gamePath = freshFolder;
                r.GamePath = freshFolder;
                pendingNote = "Your game now lives in " + freshFolder + ", with your save. Your old folder was only read; delete it when you're happy.";
            }
            if (!GameScanner.Inspect(gamePath).IsBase)
                throw new StepFailed("The game's downloader finished, but " + gamePath + " isn't v" + Payload.BaseRelease + " now (its VERSION.txt).", "The full output is in the log (Show details).");
            Set(step, StepState.Done);
        }

        void OnUpstreamLine(string line, StepId step)
        {
            var m = Regex.Match(line, @"^Downloading part (\d+) of (\d+)");
            if (m.Success) { SetStatus("Downloading part " + m.Groups[1].Value + " of " + m.Groups[2].Value + "..."); return; }
            if (line.StartsWith("Checking it...")) { SetStatus(Regex.Replace(Status, "^Downloading", "Checking").TrimEnd('.') + "..."); return; }
            m = Regex.Match(line, @"^Unpacking part (\d+) of (\d+)");
            if (m.Success) { SetStatus("Unpacking part " + m.Groups[1].Value + " of " + m.Groups[2].Value + "..."); return; }
            m = Regex.Match(line, @"^Update v(\S+) -> v(\S+): (.+)$");
            if (m.Success) { SetStatus("Updating v" + m.Groups[1].Value + " to v" + m.Groups[2].Value + "...", m.Groups[3].Value); return; }
            if (line.StartsWith("Making a fresh") || line.StartsWith("This installs ")) { SetStatus("Installing a fresh copy..."); return; }
            if (line.StartsWith("Bringing your save over")) { SetStatus("Bringing your save over..."); return; }
            if (line.StartsWith("Your game is already up to date")) Progress(step, 1);
        }

        /// <summary>
        /// Polls the downloader's cache and the game folder twice a second for real progress numbers. A new install's parts
        /// are unpacked as they arrive and then deleted, with a "&lt;part&gt;.done" marker in the game's .install folder, so
        /// done = the finished parts' sizes (from the manifest) + the part downloading now.
        /// </summary>
        sealed class DownloadWatch : IDisposable
        {
            readonly string cache, game;
            readonly StepId step;
            readonly Engine e;
            Timer timer;
            readonly Queue<(DateTime t, long b)> samples = new Queue<(DateTime, long)>();
            bool manifestRead;
            int busy;

            public DownloadWatch(string cache, string game, StepId step, Engine e) { this.cache = cache; this.game = game; this.step = step; this.e = e; }
            public void Start() => timer = new Timer(_ => Tick(), null, 500, 500);
            public void Stop() { timer?.Dispose(); timer = null; }
            public void Dispose() => Stop();

            void Tick()
            {
                if (Interlocked.Exchange(ref busy, 1) == 1) return;
                try { Measure(); }
                catch (Exception ex) { Log.Write("progress: " + ex.Message); }
                finally { Interlocked.Exchange(ref busy, 0); }
            }

            void Measure()
            {
                if (!Directory.Exists(cache)) return;
                var manifestFile = Path.Combine(cache, "download-manifest.json");
                if (!manifestRead && File.Exists(manifestFile))
                {
                    try { Manifests.Apply(File.ReadAllText(manifestFile)); manifestRead = true; } catch { }
                }
                long total = step == StepId.Download ? Manifests.Download : Manifests.Patch;
                var marks = Path.Combine(game, step == StepId.Download ? ".install" : ".patch");
                long done = 0;
                var finished = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(marks))
                    foreach (var m in Directory.GetFiles(marks, "*.zip.done"))
                    {
                        var part = Path.GetFileNameWithoutExtension(m);   // "<part>.zip"
                        finished.Add(part);
                        done += Manifests.PartBytes.TryGetValue(part, out var b) ? b : 0;
                    }
                foreach (var f in Directory.GetFiles(cache, "*.zip"))
                {
                    if (finished.Contains(Path.GetFileName(f))) continue;
                    try { done += new FileInfo(f).Length; } catch { }
                }
                // curl.exe writes to the part's own name; PowerShell's fallback to a temporary one beside it
                foreach (var f in Directory.GetFiles(cache, "*.zip.*"))
                    try { done += new FileInfo(f).Length; } catch { }

                var now = DateTime.UtcNow;
                samples.Enqueue((now, done));
                while (samples.Count > 2 && (now - samples.Peek().t).TotalSeconds > 8) samples.Dequeue();
                double rate = 0;
                if (samples.Count > 1)
                {
                    var first = samples.Peek();
                    var secs = (now - first.t).TotalSeconds;
                    if (secs > 0.5) rate = Math.Max(0, (done - first.b) / secs);
                }
                var s = e.Find(step);
                if (s == null || s.Item.State != StepState.Active || total <= 0 || done <= 0) return;
                e.Progress(step, done / (double)total * 0.97);
                e.Detail = Line(done, total, rate);
                e.Changed?.Invoke();
            }

            static string Line(long done, long total, double rate)
            {
                var s = Theme.Gb(Math.Min(done, total)) + " of " + Theme.Gb(total);
                if (rate > 1024)
                {
                    s += "  ·  " + (rate / 1048576.0).ToString(rate > 10485760 ? "0" : "0.0") + " MB/s";
                    var left = (total - done) / rate;
                    if (left > 1) s += "  ·  " + (left < 90 ? "about " + Math.Max(1, (int)Math.Round(left / 5) * 5) + " s left" : left < 5400 ? "about " + (int)Math.Round(left / 60) + " min left" : "about " + (left / 3600).ToString("0.0") + " h left");
                }
                return s;
            }
        }

        public static long DirSize(string dir)
        {
            long total = 0;
            try
            {
                if (!Directory.Exists(dir)) return 0;
                var stack = new Stack<DirectoryInfo>();
                stack.Push(new DirectoryInfo(dir));
                while (stack.Count > 0)
                {
                    var d = stack.Pop();
                    try
                    {
                        foreach (var f in d.EnumerateFiles()) { try { total += f.Length; } catch { } }
                        foreach (var s in d.EnumerateDirectories()) if ((s.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(s);
                    }
                    catch { }
                }
            }
            catch { }
            return total;
        }

        // ---------------------------------------------------------------- the Living Server package's scripts

        /// <summary>Undoes every Living Server install in the folder, newest first, down to the released files. Returns how many.</summary>
        int RollBackAll(string folder)
        {
            int undone = 0;
            for (int left = GameScanner.InstallRecords(folder).Count; left > 0 && undone < 20; left--)
            {
                var saved = gamePath; gamePath = folder;
                try { RunPackageScript("rollback-server-build.ps1", "-GameFolder " + ScriptJob.Quote(folder)); }
                finally { gamePath = saved; }
                undone++;
            }
            if (undone == 0) throw new StepFailed("This game has no Living Server install to undo.", "Its server and launcher are the released ones already.");
            return undone;
        }

        void RunPackageScript(string script, string args)
        {
            string dir;
            try { dir = Payload.ExtractPackage(gamePath); }
            catch (Exception e) { throw new StepFailed("Couldn't unpack Living Server into the game folder: " + e.Message, "Check that the folder isn't read-only and that antivirus isn't blocking it, then try again."); }
            Log.Write("Living Server package unpacked to " + dir);
            var job = new ScriptJob();
            string refusal = null, lastGreen = null;
            job.Line += (line, err) =>
            {
                var m = Regex.Match(line, @"NOTHING WAS CHANGED:\s*(.+)$");
                if (m.Success) refusal = m.Groups[1].Value.Trim();
                if (line.StartsWith("Living Server ") && (line.Contains(" is installed") || line.Contains(" is already installed") || line.Contains(" was undone"))) lastGreen = line;
            };
            var code = job.Run(Path.Combine(dir, script), args + " -NoPause", CancellationToken.None);
            if (code != 0)
            {
                if (refusal != null)
                {
                    var f = new StepFailed(refusal);
                    if (refusal.Contains("-Force") && !o.ForceLivingServer)
                    {
                        f.CanForce = true;
                        f.Hint = "Setup can replace them anyway: the files there now are backed up first, and ROLLBACK.cmd in the " + Payload.RootFolder + " folder puts them back.";
                    }
                    else if (refusal.Contains("close the game")) f.Hint = "Quit the game, stop the server and close the launcher, then click Try again.";
                    throw f;
                }
                throw new StepFailed(job.LastError ?? (script + " stopped with code " + code + "."), "The full output is in the log (Show details).");
            }
            if (lastGreen != null) Log.Write(lastGreen);
        }

        /// <summary>After a failed or stopped update the game is as it was (the game's updater puts everything back), but
        /// Living Server was taken off for it: put the earlier build back.</summary>
        void RestoreAfterFailedUpdate(InstallResult r)
        {
            if (!rolledBack) return;
            rolledBack = false;
            var note = RestorePrevious(gamePath);
            r.Message += " " + (note ?? "Your game is unchanged, but its released server is in place now: run Setup again to add Living Server.");
        }

        /// <summary>Puts the newest build that was installed before back into a folder, from its package folder there.
        /// Returns a sentence for the player, or null.</summary>
        string RestorePrevious(string folder)
        {
            try
            {
                var g = GameScanner.Inspect(folder);
                if (g.Ready)
                {
                    Log.Write("Putting Living Server back in " + folder + "...");
                    var saved = gamePath; gamePath = folder;
                    try { RunPackageScript("install-server-build.ps1", "-GameFolder " + ScriptJob.Quote(folder)); }
                    finally { gamePath = saved; }
                    return "Living Server " + Payload.DisplayName + " is installed in " + folder + " again.";
                }
                var previous = o.Game?.LsBuild;
                if (previous == null) return null;
                foreach (var info in Directory.GetDirectories(folder).Select(d => Path.Combine(d, "BUILD-INFO.txt")).Where(File.Exists))
                {
                    if (!Regex.IsMatch(File.ReadAllText(info), @"(?m)^BuildName=" + Regex.Escape(previous) + @"\s*$")) continue;
                    var installer = Path.Combine(Path.GetDirectoryName(info), "install-server-build.ps1");
                    if (!File.Exists(installer)) break;
                    Log.Write("Putting " + previous + " back in " + folder + "...");
                    if (new ScriptJob().Run(installer, "-GameFolder " + ScriptJob.Quote(folder) + " -NoPause", CancellationToken.None) == 0)
                        return "Your " + Wizard.BuildLabel(previous) + " build is back in " + folder + ", as before.";
                    break;
                }
                return "In " + folder + " the released server is in place now: " + Wizard.BuildLabel(previous) + " is off until it's installed again.";
            }
            catch (Exception e)
            {
                Log.Write("Restoring the earlier build: " + e.Message);
                return "Putting the earlier build back didn't work (" + e.Message + "): run Setup again.";
            }
        }
    }

    internal static class Shortcuts
    {
        public static void Create(string game)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Offline DAoC True 1.65");
            Directory.CreateDirectory(menu);
            // The shortcuts carry Living Server's own icon, written beside the launcher; the game's launcher and its exe
            // icon are left as they are.
            var icon = Path.Combine(game, "runtime", "OfflineDAoC.exe");
            try
            {
                var ours = Path.Combine(game, "runtime", "living-server.ico");
                using (var s = Theme.Resource("shortcut.ico")) using (var f = File.Create(ours)) s.CopyTo(f);
                icon = ours;
            }
            catch (Exception e) { Log.Write("Shortcut icon: " + e.Message + " (the launcher's icon is used)"); }
            var target = Path.Combine(game, GameScanner.StartFile);
            if (File.Exists(target))
                foreach (var dir in new[] { desktop, menu })
                    Make(Path.Combine(dir, "Offline DAoC True 1.65.lnk"), target, game, File.Exists(icon) ? icon : null, "Play Offline DAoC True 1.65 with Living Server");
            Make(Path.Combine(menu, "Offline DAoC True 1.65 game folder.lnk"), game, game, null, "Open the Offline DAoC True 1.65 folder");
            Log.Write("Shortcuts added on the desktop and in Start > Offline DAoC True 1.65.");
        }

        static void Make(string lnk, string target, string workDir, string icon, string description)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host isn't available.");
            dynamic shell = Activator.CreateInstance(type);
            try
            {
                dynamic s = shell.CreateShortcut(lnk);
                s.TargetPath = target;
                s.WorkingDirectory = workDir;
                s.Description = description;
                if (icon != null) s.IconLocation = icon.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ? icon : icon + ",0";
                s.Save();
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(s);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
    }
}
