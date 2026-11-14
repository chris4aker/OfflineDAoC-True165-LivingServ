using System;

namespace DOL.GS;

public static class RealmRaidRecruitmentPolicy
{
    public const int MaximumBots = 300;
    public const int MaximumParties = (MaximumBots + 7) / 8;
    /// <summary>Formation slots a raid has: the realm's 38 parties plus the Epic Special Forces' three (Living Server, off by
    /// default; with them off no party takes a slot past the realm's 38).</summary>
    public const int FormationSlots = MaximumParties + AutonomousEpicSpecialForces.MaximumParties;

    /// <summary>
    /// Why a raid cannot take another party, or null when it can. The realm's limits (38 parties, 300 bots) count only
    /// the realm's own parties and bots; the Epic Special Forces come on top of them, up to their own three parties.
    /// </summary>
    public static string PartyCapRefusal(bool specialForces, int realmParties, int realmBots, int specialForcesParties, int joining) =>
        specialForces
            ? specialForcesParties >= AutonomousEpicSpecialForces.MaximumParties
                ? $"the raid already has {specialForcesParties} Special Forces parties" : null
            : realmParties >= MaximumParties ? $"the raid already has its {MaximumParties} parties"
            : realmBots + joining > MaximumBots ? $"the raid would have {realmBots + joining} of its {MaximumBots} bots" : null;
    public const int AutonomousMinimumPresent = 200;
    public const int MinimumParties = AutonomousMinimumPresent / 8;
    public const long AutonomousMinimumStagingMilliseconds = 15 * 60_000L;
    public const long AutonomousStagingLimitMilliseconds = 90 * 60_000L;
    public const long BattleMilliseconds = 4 * 60 * 60_000L;
    public const double NewEventChance = .20;
    public const double JoinExistingChance = .95;

    // An automatic rally that has waited most of its window with a nearly full
    // roster goes in rather than failing outright. Caer Sidi failed twice in one
    // run with 196 and 186 of the 200 required bots at its entrance.
    public const long AutonomousLateStartMilliseconds = 75 * 60_000L;
    public const int AutonomousLateStartMinimumPresent = 180;

    public static bool Eligible(int level, bool autonomous, bool temporary, bool playerLed) =>
        level == 50 && autonomous && !temporary && !playerLed;

    public static bool CanOpenEvent(bool forced, bool sameRealmEvent, bool sameEncounterEvent) =>
        !sameEncounterEvent && (forced || !sameRealmEvent);

    // Launcher-forced raids go in as soon as 200 are staged and, for a dragon, it has landed. They have no staging or
    // battle time limit: they run until the encounter is defeated, Stop event is pressed, or the server stops. Horn
    // raids have their own clock (HornDepartHub ... HornBattleStalled below).
    public static bool Ready(bool forced, long elapsed, int present, bool landed) =>
        elapsed >= (forced ? 0 : AutonomousMinimumStagingMilliseconds) &&
        (present >= AutonomousMinimumPresent ||
         !forced && elapsed >= AutonomousLateStartMilliseconds && present >= AutonomousLateStartMinimumPresent) &&
        landed;

    // 2026-10-16 (owner's session): the automatic Tuscaran rally needed 84 minutes to gather 200 at Hagall, so the one
    // 90-minute staging clock left 6 minutes for the trip to the dungeon and it failed with 93 there. A slow hub leaves
    // after HubPatienceMilliseconds with HubEarlyDepartureMinimum present (late arrivals join the expedition directly),
    // and after leaving the raid always gets PostDepartureStagingMilliseconds to stage.
    public const long HubPatienceMilliseconds = 30 * 60_000L;
    public const int HubEarlyDepartureMinimum = 120;
    public const long PostDepartureStagingMilliseconds = 45 * 60_000L;

    public static bool DepartHub(bool alreadyDeparted, int presentBots, long elapsed = 0) =>
        alreadyDeparted || presentBots >= AutonomousMinimumPresent ||
        elapsed >= HubPatienceMilliseconds && presentBots >= HubEarlyDepartureMinimum;

    public static bool StagingExpired(bool forced, long elapsed, int present = 0, bool landed = true) =>
        !forced && elapsed >= AutonomousStagingLimitMilliseconds &&
        !(present >= AutonomousMinimumPresent && !landed);

    /// <summary>When an automatic rally's staging ends: 90 minutes after it opened, but never sooner than 45 minutes
    /// after its parties left the hub (departedTick 0 = not yet).</summary>
    public static long StagingDeadline(long created, long departedTick) =>
        Math.Max(created + AutonomousStagingLimitMilliseconds,
            departedTick > 0 ? departedTick + PostDepartureStagingMilliseconds : 0);

    public static bool StagingExpired(bool forced, long now, long created, long departedTick, int present, bool landed) =>
        !forced && now >= StagingDeadline(created, departedTick) &&
        !(present >= AutonomousMinimumPresent && !landed);

    // Owner 2026-10-29: rallies failed with bots still arriving (Albion dragon: 170 there, 84 on the way). A bot that
    // arrives with less than 10 minutes left gives the rally 10 more minutes, up to 30 minutes past its deadline.
    public const long ArrivalExtensionMilliseconds = 10 * 60_000L;
    public const long MaximumExtensionMilliseconds = 30 * 60_000L;

    /// <summary>The staging deadline after arrivals: lastArrival + 10 min when that is later, never past deadline + 30 min.</summary>
    public static long ExtendedStagingDeadline(long deadline, long lastArrivalTick) =>
        lastArrivalTick <= 0 ? deadline :
        Math.Min(deadline + MaximumExtensionMilliseconds, Math.Max(deadline, lastArrivalTick + ArrivalExtensionMilliseconds));

    public static bool StagingExpired(bool forced, long now, long created, long departedTick, int present, bool landed,
        long lastArrivalTick) =>
        !forced && now >= ExtendedStagingDeadline(StagingDeadline(created, departedTick), lastArrivalTick) &&
        !(present >= AutonomousMinimumPresent && !landed);

    public static bool BattleExpired(bool forced, long now, long deadline) => !forced && now >= deadline;

    /// <summary>A raid party that lost members (but still exists) takes replacements up to its size when it joined.</summary>
    public static bool NeedsReplacement(int target, int members) => members > 0 && members < target;

    // ---------- Horn raids (realm event audit, 2026-11-12) ----------
    // A horn raid used to have no end of its own: no muster or staging deadline, no battle limit, so a raid short of 200
    // (or one that wiped at its final boss) held up to 300 level-50 bots until a restart. The hub left with 120 after
    // 30 minutes while the assault needed 200 staged. The numbers below agree with each other: the hub and the assault
    // both go with 300; with 200+ once nobody new has arrived for a while; with 160+ (four fifths of the 200 the horn
    // needs) when everyone still expected is there or the patience is spent. Below 160 at a deadline the rally fails.

    /// <summary>The smallest horn raid that goes in: when nobody else is coming, or at a deadline.</summary>
    public const int HornShortMinimum = 160;
    /// <summary>A horn muster that has not left its hub this long after the sound fails.</summary>
    public const long HornMusterLimitMilliseconds = 45 * 60_000L;
    /// <summary>The hub leaves with 200+ once nobody new has stood at it for this long.</summary>
    public const long HornHubQuietMilliseconds = 2 * 60_000L;
    /// <summary>The assault begins with 200+ staged once nobody new has arrived for this long...</summary>
    public const long HornStagingQuietMilliseconds = 3 * 60_000L;
    /// <summary>...or this long after 200 were first staged, whatever is still on the road.</summary>
    public const long HornStagingWaitCapMilliseconds = 15 * 60_000L;

    /// <summary>A horn raid leaves its hub: 300 there; everyone expected there (at least 160); 200+ and nobody new for two
    /// minutes; or after 30 minutes with 160+.</summary>
    public static bool HornDepartHub(int present, int coming, long sinceSound, long sinceLastHubArrival) =>
        present >= MaximumBots ||
        present >= HornShortMinimum && present >= coming ||
        present >= AutonomousMinimumPresent && sinceLastHubArrival >= HornHubQuietMilliseconds ||
        present >= HornShortMinimum && sinceSound >= HubPatienceMilliseconds;

    /// <summary>A horn muster still at its hub 45 minutes after the sound without 160 there is called off.</summary>
    public static bool HornMusterFailed(bool departed, int present, long sinceSound) =>
        !departed && sinceSound >= HornMusterLimitMilliseconds && present < HornShortMinimum;

    /// <summary>A horn raid's staging deadline: 45 minutes after leaving the hub, 10 more for each late arrival, at most
    /// 30 past it (the automatic rally's clock).</summary>
    public static long HornStagingDeadline(long departedTick, long lastArrivalTick) =>
        ExtendedStagingDeadline(departedTick + PostDepartureStagingMilliseconds, lastArrivalTick);

    /// <summary>The horn assault begins (the dragon landed): 300 staged; everyone expected there (at least 160); 200+ once
    /// nobody new arrived for 3 minutes or 15 minutes after 200 were first staged; or 160+ at the staging deadline.</summary>
    public static bool HornAssaultReady(int present, int coming, long minimumReachedTick, long lastArrivalTick, bool deadlinePassed,
        long now, bool landed) =>
        landed && (present >= MaximumBots ||
                   present >= HornShortMinimum && (present >= coming || deadlinePassed) ||
                   present >= AutonomousMinimumPresent &&
                   (lastArrivalTick > 0 && now - lastArrivalTick >= HornStagingQuietMilliseconds ||
                    minimumReachedTick > 0 && now - minimumReachedTick >= HornStagingWaitCapMilliseconds));

    /// <summary>At the staging deadline with fewer than 160 staged the rally fails (160+ waiting on a dragon's landing
    /// keeps waiting: the flight is at most a quarter of an hour).</summary>
    public static bool HornStagingFailed(int present, bool deadlinePassed) =>
        deadlinePassed && present < HornShortMinimum;

    /// <summary>A horn battle ends "Failed" after this many engagements of the final encounter that ended with it back out
    /// of combat at full health (a wipe).</summary>
    public const int HornFailedFinalEngagements = 3;
    /// <summary>A horn battle ends "Timed out" after this long without a route target or named boss killed and without the
    /// final encounter reaching a new low.</summary>
    public const long HornBattleStallMilliseconds = 60 * 60_000L;

    public static bool HornBattleFailed(int failedFinalEngagements) => failedFinalEngagements >= HornFailedFinalEngagements;

    public static bool HornBattleStalled(long battleStartTick, long lastProgressTick, long now) =>
        battleStartTick > 0 && now - Math.Max(battleStartTick, lastProgressTick) >= HornBattleStallMilliseconds;

    /// <summary>One pulse of the wipe watch on a final encounter: <paramref name="engaged"/> is set while it is hurt or
    /// fighting; back out of combat at full health after that is one failed engagement.</summary>
    public static bool FinalEngagementWiped(ref bool engaged, bool alive, bool inCombat, int healthPercent)
    {
        if (!alive) { engaged = false; return false; }
        if (inCombat || healthPercent < 100) { engaged = true; return false; }
        if (!engaged) return false;
        engaged = false;
        return true;
    }

    /// <summary>After a battle that did not kill the final encounter (route blocked, failed, timed out): a short pause
    /// before the event may be sounded again.</summary>
    public const long FailedBattleCooldownMilliseconds = 10 * 60_000L;

    /// <summary>The event's own cooldown. None after a kill (the encounter's respawn is the wait), after a player stop, or
    /// after a rally that never went in; 10 minutes after a battle that did not kill the final encounter.</summary>
    public static long EventCooldownMilliseconds(bool stoppedByPlayer, bool battleBegan, bool finalDefeated) =>
        stoppedByPlayer || finalDefeated || !battleBegan ? 0 : FailedBattleCooldownMilliseconds;

    /// <summary>Darkness Falls lost to the realm during the battle: nobody can come back in, so the raid ends once fewer than
    /// 8 living raiders have been inside for 5 minutes.</summary>
    public const long AccessLostAbandonMilliseconds = 5 * 60_000L;
    public const int AccessLostMinimumInside = 8;

    public static bool AccessLostAbandoned(long fewInsideSinceTick, long now) =>
        fewInsideSinceTick > 0 && now - fewInsideSinceTick >= AccessLostAbandonMilliseconds;
}
