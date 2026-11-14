using System;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS;

/// <summary>
/// /allrelease (Living Server 1.1, port): every dead gamebot in a player's group releases at once, as its own death timer
/// would. True 1.65 keeps its own rules for where a gamebot releases and how it comes back; hired companions and /spawn
/// helpers keep theirs and are never released by it.
/// </summary>
public static class BotGroupRelease
{
    private static readonly Logging.Logger Log = Logging.LoggerManager.Create(typeof(BotGroupRelease));

    /// <summary>What /allrelease tells the player.</summary>
    public static IReadOnlyList<string> Report(IReadOnlyCollection<string> released, IReadOnlyCollection<string> waiting)
    {
        if (released.Count == 0 && waiting.Count == 0)
            return ["There are no dead bots in your group."];
        List<string> lines = [];
        if (released.Count > 0)
            lines.Add($"Released: {string.Join(", ", released)}.");
        if (waiting.Count > 0)
            lines.Add($"Could not release yet: {string.Join(", ", waiting)}. Companions keep their own rules.");
        return lines;
    }

    /// <summary>Releases every dead bot of the group at once. Returns the names released and those that have to wait.</summary>
    public static (List<string> Released, List<string> Waiting) ReleaseDeadBots(Group group)
    {
        List<string> released = [];
        List<string> waiting = [];
        foreach (GameBot bot in group.GetMembersInTheGroup().OfType<GameBot>().Where(bot => !bot.IsAlive))
        {
            bool done;
            try
            {
                done = bot.ReleaseNow();
            }
            catch (Exception exception)
            {
                // The bot's own death timer goes on and retries.
                Log.Error($"BOT_GROUP_RELEASE_FAILED bot=\"{bot.Name}\"", exception);
                done = false;
            }
            (done ? released : waiting).Add(bot.Name);
        }
        return (released, waiting);
    }
}
