using System;
using System.Linq;
using System.Reflection;
using DOL.Events;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS;

/// <summary>
/// Names this server build in the log at startup: the Living Server build, the True 1.65 release it sits on, the git
/// commit it was built from, and which Living Server features are switched on. One line, so a log shows at a glance
/// what was running (players paste it into bug reports).
/// </summary>
public static class ForkBuildInfo
{
    /// <summary>The Living Server build name (Living Server for True 1.65); bump it with each release.</summary>
    public const string BuildName = "1.1.4-beta";

    /// <summary>
    /// The Offline DAoC True 1.65 release(s) this build installs on; the installer checks the game against them.
    /// Comma-separated once the server code carries a newer release's server changes, e.g. "0.7,0.8".
    /// </summary>
    public const string BaseRelease = "0.7";

    /// <summary>The game this edition is for, as the installer and the update check name it.</summary>
    public const string Edition = "true165";

    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>The git commit, from the informational version CI stamps (<c>-p:SourceRevisionId=sha</c>); "local" otherwise.</summary>
    public static string Commit => CommitFrom(typeof(ForkBuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string CommitFrom(string informationalVersion)
    {
        int plus = informationalVersion?.IndexOf('+') ?? -1;
        if (plus < 0 || plus == informationalVersion.Length - 1)
            return "local";
        string sha = informationalVersion[(plus + 1)..];
        return sha.Length > 12 ? sha[..12] : sha;
    }

    /// <summary>The startup line, from the current settings.</summary>
    public static string Describe()
    {
        string On(bool value) => value ? "on" : "off";
        string livingParts = string.Join(",", new[]
        {
            ("memory", Properties.BOT_LIVING_MEMORY), ("grats", Properties.BOT_LIVING_GRATS), ("firsts", Properties.BOT_LIVING_FIRSTS),
            ("death", Properties.BOT_LIVING_DEATH), ("alarms", Properties.BOT_LIVING_ALARMS), ("nemesis", Properties.BOT_LIVING_NEMESIS),
            ("deeds", Properties.BOT_LIVING_DEEDS), ("storylines", Properties.BOT_LIVING_STORYLINES),
            ("characters", Properties.BOT_LIVING_CHARACTERS), ("rumours", Properties.BOT_LIVING_RUMOURS),
            ("holidays", Properties.BOT_LIVING_HOLIDAYS), ("beggars", Properties.BOT_LIVING_BEGGARS),
            ("bonds", Properties.BOT_GROUP_BONDS), ("breaks", Properties.BOT_LIVING_BREAKS), ("body", Properties.BOT_LIVING_BODY_LANGUAGE), ("leeroy", Properties.BOT_LIVING_LEEROY),
        }.Where(part => part.Item2).Select(part => part.Item1));
        return $"FORK_BUILD name={BuildName} edition={Edition} base={BaseRelease} commit={Commit}" +
               $" bot_personalities={On(Properties.BOT_PERSONALITIES_ENABLED)} bot_voices={On(Properties.BOT_VOICES_ENABLED)}" +
               (Properties.BOT_VOICES_ENABLED ? $" bot_debates={On(Properties.BOT_VOICES_DEBATES)} bot_tells={On(Properties.BOT_VOICES_TELLS)}" : string.Empty) +
               $" bot_guilds={On(Properties.BOT_GUILDS_ENABLED)}" +
               $" living={(livingParts.Length == 0 ? "off" : livingParts)} bot_schedule={AutonomousBotSchedule.CurrentMode.ToString().ToLowerInvariant()}" +
               $" incoming_fire={On(Properties.BOT_INCOMING_FIRE_REACTION)} era_names={On(Properties.BOT_ERA_NAMES)}" +
               $" xp_by_level={(string.IsNullOrWhiteSpace(Properties.XP_RATE_BY_LEVEL) && string.IsNullOrWhiteSpace(Properties.BOT_XP_RATE_BY_LEVEL) ? "off" : "on")}" +
               $" quest_xp_by_level={On(Properties.QUEST_XP_BY_LEVEL)}";
    }

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        try
        {
            // Warn level so it shows even when the log is set to warnings only.
            if (Log.IsWarnEnabled)
                Log.Warn(Describe());
        }
        catch (Exception exception)
        {
            Log.Error("FORK_BUILD: could not describe this build", exception);
        }
    }
}
