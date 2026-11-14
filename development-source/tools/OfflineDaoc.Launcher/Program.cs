namespace OfflineDaoc.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string serverRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (args.Length == 1 && args[0] == "--apply-recommended-settings")
        {
            // Living Server Setup, after a first install: this edition's parts on (SocialExpansionSettings.Recommended).
            Environment.ExitCode = ApplyRecommendedSettings(serverRoot);
            return;
        }
        if (args.Length >= 2 && args[0] == "--apply-update")
        {
            // Tests and support: a Living Server update from a package on disk, without the window (Updates/ApplyUpdateCommand.cs).
            Environment.ExitCode = Updates.ApplyUpdateCommand.Run(args, serverRoot);
            return;
        }
        // --refresh-165: update the True 1.65 world content in place (the same step as a start in that mode) and exit;
        // for scripts and test runs. Writes data\true165-refresh.log. Never with the server running.
        if (args.Any(a => a.Equals("--refresh-165", StringComparison.OrdinalIgnoreCase)))
        {
            string root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string log = Path.Combine(root, "data", "true165-refresh.log");
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName("CoreServer").Length > 0)
                    throw new InvalidOperationException("the server is running");
                bool changed = TrueMode.Refresh(root, message => File.AppendAllText(log, $"{DateTime.Now:s} {message}\r\n"));
                File.AppendAllText(log, $"{DateTime.Now:s} {(changed ? "updated" : "already current")}\r\n");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.AppendAllText(log, $"{DateTime.Now:s} FAILED: {ex.Message}\r\n");
                Environment.Exit(1);
            }
        }
        // --population-goals <Albion> <Midgard> <Hibernia>: sets the realm goals headless (scripts and test runs).
        // Allowed while the server runs; it picks them up within seconds. Writes data\population-goals.log.
        // It replaces --add-bots: bots are no longer made by the launcher; the server creates level-1 bots to meet the goals.
        int goalsAt = Array.FindIndex(args, a => a.Equals("--population-goals", StringComparison.OrdinalIgnoreCase) ||
                                                 a.Equals("--add-bots", StringComparison.OrdinalIgnoreCase));
        if (goalsAt >= 0)
        {
            string root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string log = Path.Combine(root, "data", "population-goals.log");
            try
            {
                if (args[goalsAt].Equals("--add-bots", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--add-bots was removed: the server creates level-1 bots itself. Use --population-goals <Albion> <Midgard> <Hibernia>.");
                if (args.Length < goalsAt + 4)
                    throw new ArgumentException("usage: --population-goals <Albion> <Midgard> <Hibernia>");
                string stateDirectory = Path.Combine(root, "server", "state-165");
                DOL.GS.PopulationGoalSettings.TryLoad(stateDirectory, out DOL.GS.PopulationGoalSettings current, out _);
                DOL.GS.PopulationGoalSettings next = (current with
                {
                    Albion = int.Parse(args[goalsAt + 1]), Midgard = int.Parse(args[goalsAt + 2]), Hibernia = int.Parse(args[goalsAt + 3]),
                }).WithNewRevision();
                DOL.GS.PopulationGoalSettings.Save(stateDirectory, next);   // validates (0-5,000 a realm, 15,000 in total)
                if (next.Total > 0)
                    PopulationLauncherData.EnsurePopulationEnabled(TrueMode.Describe(root, true).DatabasePath);
                File.AppendAllText(log, $"{DateTime.Now:s} goals Albion {next.Albion}, Midgard {next.Midgard}, Hibernia {next.Hibernia} (revision {next.Revision})\r\n");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                File.AppendAllText(log, $"{DateTime.Now:s} FAILED: {ex.Message}\r\n");
                Environment.Exit(1);
            }
        }
        ApplicationConfiguration.Initialize();
        // OfflineDAoC True 1.65 has no normal mode (owner 2026-10-31): no --mode= switch any more.
        Application.Run(new MainForm());
    }

    private static int ApplyRecommendedSettings(string serverRoot)
    {
        string database = TrueMode.Describe(serverRoot, true).DatabasePath;
        try
        {
            bool stopped = System.Diagnostics.Process.GetProcessesByName("CoreServer").Length == 0;
            SocialExpansionSettings.Recommended.Save(database, null, () => stopped);
            var problems = new List<string>();
            bool saved = SocialExpansionSettings.Load(database, problems) == SocialExpansionSettings.Recommended && problems.Count == 0;
            Console.WriteLine(saved ? "Recommended Living Server settings saved in " + database
                                    : "Saved, but reading back differs: " + string.Join("; ", problems));
            return saved ? 0 : 2;
        }
        catch (Exception e)
        {
            Console.WriteLine("Recommended settings not saved: " + e.Message);
            return 1;
        }
    }
}
