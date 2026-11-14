using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DOL.Events;

namespace DOL.GS;

// Observational only: lifecycle hooks update tiny immutable records in memory.
// A separate timer writes the separate ledger; no SQL or file I/O on AI turns.
public static class RealmEventRecords
{
    private static readonly object Sync=new(), WriteSync=new();
    private static readonly Dictionary<string,RealmEventRecord> Active=new(), Pending=new();
    private static Timer _writer;
    private static bool _reconcile;
    private static DateTime _nextWarningUtc;
    private static string PathName=>Classic165Mode.StatePath("realm-event-records.sqlite3");
    public static void Begin(string eventId,string name,string kind,string realm,string details)
    {
        lock(Sync)
        {
            if(Active.ContainsKey(eventId))return;
            if(Pending.Count>=4096){DOL.Logging.LoggerManager.Create(typeof(RealmEventRecords)).Warn("Realm event ledger backlog full; new record skipped, gameplay unchanged.");return;}
            var row=new RealmEventRecord(Guid.NewGuid().ToString("N"),eventId,name,kind,realm,DateTime.UtcNow.ToString("O"),"","Rally","In progress",details,0,0);
            Active[eventId]=row;Pending[row.Id]=row;Battles[eventId]=new Battle{Details=details};
        }
    }
    public static void Progress(string eventId,string phase,string details,int assigned,int present)
    {
        lock(Sync)if(Active.TryGetValue(eventId,out var row))
        {
            Battle battle=BattleOf(eventId,details);
            if(phase=="Battle")battle.StartedUtc??=DateTime.UtcNow;
            row=row with {Phase=phase,Details=Compose(battle,DateTime.UtcNow,false),Assigned=assigned,Present=present};Active[eventId]=row;Pending[row.Id]=row;
        }
    }
    /// <summary>An event's finished runs with how many Epic Special Forces joined each (RealmEventRecordStore.Results).
    /// File I/O: off the game loop.</summary>
    public static List<(string Outcome,int SpecialForces)> FinishedResults(string eventId,int limit,bool automaticOnly=false)
    {
        try{return RealmEventRecordStore.Results(PathName,eventId,limit,automaticOnly);}
        catch(Exception){return new();}
    }
    public static void Finish(string eventId,string outcome,string details,int assigned=-1,int present=-1)
    {
        lock(Sync)if(Active.Remove(eventId,out var row))
        {
            Battle battle=BattleOf(eventId,details);Battles.Remove(eventId);
            row=row with {EndedUtc=DateTime.UtcNow.ToString("O"),Phase="Ended",Outcome=outcome,Details=Compose(battle,DateTime.UtcNow,true),Assigned=assigned<0?row.Assigned:assigned,Present=present<0?row.Present:present};Pending[row.Id]=row;
        }
    }

    // Audits 2026-11-11 (dragon-fight M3, df-bosses M2, sh-bosses M7): the ledger could not show how long a fight
    // lasted, which bosses fell or were set aside, or how many died. The battle start is stamped when the event
    // enters its "Battle" phase; a death only counts in memory (written with the next row update, never a write of
    // its own); an encounter result updates the row, a few dozen times per raid at most. All three go into Details,
    // so the ledger's columns and the launcher's reader stay as they are.
    private sealed class Battle{public string Details="";public DateTime? StartedUtc;public int Deaths;public readonly List<string> Encounters=new();public int EncountersOmitted;}
    private static readonly Dictionary<string,Battle> Battles=new();
    public const int MaximumEncounterEntries=48;
    private static Battle BattleOf(string eventId,string details)
    {
        if(!Battles.TryGetValue(eventId,out var battle))Battles[eventId]=battle=new Battle();
        if(details!=null)battle.Details=details;
        return battle;
    }
    private static string Compose(Battle battle,DateTime nowUtc,bool ended)=>
        ComposeDetails(battle.Details,battle.StartedUtc,nowUtc,ended,battle.Deaths,battle.Encounters,battle.EncountersOmitted);

    /// <summary>A raider of this event died (counted once per death).</summary>
    public static void CountDeath(string eventId)
    {
        lock(Sync)if(Active.ContainsKey(eventId))BattleOf(eventId,null).Deaths++;
    }

    /// <summary>A named encounter of this event fell ("defeated") or was set aside ("skipped").</summary>
    public static void Encounter(string eventId,string name,string result)
    {
        lock(Sync)if(Active.TryGetValue(eventId,out var row))
        {
            Battle battle=BattleOf(eventId,null);
            if(battle.Encounters.Count<MaximumEncounterEntries)battle.Encounters.Add($"{name} {result} {DateTime.UtcNow:HH:mm}");
            else battle.EncountersOmitted++;
            row=row with {Details=Compose(battle,DateTime.UtcNow,false)};Active[eventId]=row;Pending[row.Id]=row;
        }
    }

    /// <summary>The same, for the dungeon route, which knows its region and realm rather than the event id.</summary>
    public static void Encounter(ushort region,eRealm realm,string name,string result)
    {
        string id=Array.Find(AutonomousRealmRaid.Definitions,d=>d.Region==region&&d.Realm==realm)?.Id;
        if(id!=null)Encounter(id,name,result);
    }

    public static string ComposeDetails(string details,DateTime? battleStartedUtc,DateTime nowUtc,bool ended,int deaths,
        IReadOnlyList<string> encounters,int omitted)
    {
        var parts=new List<string>{details??""};
        if(battleStartedUtc is DateTime start)
            parts.Add(ended?$"Battle {start:yyyy-MM-dd HH:mm:ss}Z to {nowUtc:HH:mm:ss}Z ({(int)Math.Max(0,(nowUtc-start).TotalMinutes)} min)"
                :$"Battle began {start:yyyy-MM-dd HH:mm:ss}Z");
        if(deaths>0)parts.Add($"Deaths: {deaths}");
        if(encounters.Count>0)parts.Add("Encounters: "+string.Join(", ",encounters)+(omitted>0?$" (+{omitted} more)":""));
        return string.Join(" | ",parts.Where(p=>p.Length>0));
    }
    [GameServerStartedEvent]
    public static void Start(DOLEvent e,object sender,EventArgs args)
    {
        _writer?.Dispose();lock(Sync){Active.Clear();Pending.Clear();Battles.Clear();_reconcile=true;}
        _writer=new Timer(_=>Flush(),null,1000,2000);
    }
    [GameServerStoppedEvent]
    public static void Stop(DOLEvent e,object sender,EventArgs args)
    {
        _writer?.Dispose();_writer=null;
        lock(Sync)foreach(string id in Active.Keys.ToArray())Finish(id,"Interrupted","Server stopped before the event reached a final outcome.");
        Flush(true);
    }
    private static void Flush(bool drain = false)
    {
        if(drain)Monitor.Enter(WriteSync);
        else if(!Monitor.TryEnter(WriteSync))return;
        try
        {
            if(_reconcile){RealmEventRecordStore.CloseInterrupted(PathName);_reconcile=false;}
            do
            {
                KeyValuePair<string,RealmEventRecord>[] batch;
                lock(Sync)batch=Pending.Take(128).ToArray();
                if(batch.Length==0)return;
                RealmEventRecordStore.Save(PathName,batch.Select(p=>p.Value));
                lock(Sync)foreach(var item in batch)
                    if(Pending.TryGetValue(item.Key,out var current)&&ReferenceEquals(current,item.Value))Pending.Remove(item.Key);
            } while(drain);
        }
        catch(Exception ex)
        {
            if(DateTime.UtcNow>=_nextWarningUtc)
            { _nextWarningUtc=DateTime.UtcNow.AddMinutes(1);DOL.Logging.LoggerManager.Create(typeof(RealmEventRecords)).Warn("Realm event ledger write failed; queued records retained for retry.",ex); }
        }
        finally{Monitor.Exit(WriteSync);}
    }
}
