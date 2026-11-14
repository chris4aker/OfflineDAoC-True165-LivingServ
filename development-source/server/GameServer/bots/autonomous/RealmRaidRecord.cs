using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DOL.GS;

/// <summary>One counted result at an encounter: its outcome, and whether the Epic Special Forces took part in it.</summary>
public readonly record struct RaidResult(string Outcome, bool WithSpecialForces = false)
{
    /// <summary>An outcome alone: a raid the Special Forces did not join.</summary>
    public static implicit operator RaidResult(string outcome) => new(outcome);
    /// <summary>A win the Special Forces helped.</summary>
    public bool AssistedWin => WithSpecialForces && AutonomousEpicSpecialForces.IsWin(Outcome);
    public override string ToString() => WithSpecialForces ? Outcome + " (Special Forces)" : Outcome;
}

/// <summary>
/// Living Server port (off by default, with AutonomousEpicSpecialForces): each raid encounter's record, as far as the Epic
/// Special Forces need it: its last <see cref="Depth"/> counted results (wins and losses, oldest first) from the realm event
/// ledger, kept in memory and updated as raids end. In Living Server the same record also sizes automatic raids; a True 1.65
/// Horn raid's size is whoever answers the horn, so here it only sizes the Special Forces' call-up. Interrupted, failed
/// rallies, stopped raids and raids started from the launcher are not results. Loaded once off the game loop.
/// </summary>
public static class RealmRaidRecord
{
    public const int Depth = 400;
    /// <summary>Ledger rows read per encounter at load: restarts leave Interrupted rows between the results.</summary>
    public const int LedgerRows = 1600;

    private static readonly object Sync = new();
    private static Dictionary<string, List<RaidResult>> _record;
    private static readonly List<(string Id, RaidResult Result)> EndedBeforeLoad = new();
    private static int _loading;

    public static bool Counted(string outcome) => AutonomousEpicSpecialForces.IsWin(outcome) || AutonomousEpicSpecialForces.IsLoss(outcome);

    /// <summary>The counted results (oldest first), at most <see cref="Depth"/>.</summary>
    public static List<RaidResult> CountedResults(IEnumerable<RaidResult> resultsOldestFirst)
    {
        var counted = (resultsOldestFirst ?? []).Where(r => Counted(r.Outcome)).ToList();
        if (counted.Count > Depth) counted.RemoveRange(0, counted.Count - Depth);
        return counted;
    }

    public static bool Loaded
    {
        get { lock (Sync) return _record != null; }
    }

    /// <summary>The encounter's counted outcomes, oldest first (empty until loaded).</summary>
    public static List<string> Get(string raidId) => GetResults(raidId).Select(r => r.Outcome).ToList();

    /// <summary>The encounter's counted results with the Special Forces' part in them, oldest first (empty until loaded).</summary>
    public static List<RaidResult> GetResults(string raidId)
    {
        lock (Sync) return _record?.GetValueOrDefault(raidId)?.ToList() ?? [];
    }

    /// <summary>A raid ended: a win or a loss of a Horn raid joins its record (anything else is ignored), with whether the
    /// Special Forces joined it.</summary>
    public static void Add(string raidId, string outcome, bool forced = false, bool withSpecialForces = false)
    {
        // with the Special Forces off nothing reads the record: don't gather results (it loads from the ledger when on)
        if (forced || !Counted(outcome) || !AutonomousEpicSpecialForces.Enabled) return;
        var result = new RaidResult(outcome, withSpecialForces);
        lock (Sync)
        {
            if (_record == null) EndedBeforeLoad.Add((raidId, result));
            else Append(_record, raidId, result);
        }
    }

    private static void Append(Dictionary<string, List<RaidResult>> record, string raidId, RaidResult result)
    {
        if (!record.TryGetValue(raidId, out var list)) record[raidId] = list = new();
        list.Add(result);
        if (list.Count > Depth) list.RemoveRange(0, list.Count - Depth);
    }

    /// <summary>A ledger's rows as results: the Special Forces took part when its details name any who joined.</summary>
    public static List<RaidResult> FromLedger(IEnumerable<(string Outcome, int SpecialForces)> rowsOldestFirst) =>
        CountedResults((rowsOldestFirst ?? []).Select(row => new RaidResult(row.Outcome, row.SpecialForces > 0)));

    /// <summary>Starts loading the record from the ledger, once, off the game loop.</summary>
    public static void EnsureLoading()
    {
        if (Interlocked.Exchange(ref _loading, 1) != 0) return;
        string[] ids = AutonomousRealmRaid.Definitions.Select(d => d.Id).Distinct().ToArray();
        Task.Run(() =>
        {
            var loaded = new Dictionary<string, List<RaidResult>>(StringComparer.Ordinal);
            foreach (string id in ids)
                loaded[id] = FromLedger(RealmEventRecords.FinishedResults(id, LedgerRows, automaticOnly: true));
            lock (Sync)
            {
                foreach (var (id, result) in EndedBeforeLoad) Append(loaded, id, result);
                EndedBeforeLoad.Clear();
                _record = loaded;
            }
        });
    }

    /// <summary>Tests only: an empty, loaded record.</summary>
    public static void ResetForTests()
    {
        lock (Sync)
        {
            _record = new(StringComparer.Ordinal);
            EndedBeforeLoad.Clear();
        }
        Interlocked.Exchange(ref _loading, 1);
    }

    /// <summary>Tests only: the encounter's ledger rows, oldest first (uncounted rows are dropped as at load).</summary>
    public static void SetForTests(string raidId, params string[] outcomes) =>
        SetResultsForTests(raidId, (outcomes ?? []).Select(o => (RaidResult)o).ToArray());

    /// <summary>Tests only: the encounter's results with the Special Forces' part, oldest first.</summary>
    public static void SetResultsForTests(string raidId, params RaidResult[] results)
    {
        lock (Sync)
        {
            _record ??= new(StringComparer.Ordinal);
            _record[raidId] = CountedResults(results);
        }
    }
}
