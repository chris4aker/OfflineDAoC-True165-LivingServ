namespace DOL.GS
{
    /// <summary>
    /// Explicit offline encounter adaptations. Bot raids cannot recover safely from
    /// scripted jail/air teleports or satisfy player-only split-position puzzles.
    /// Normal attacks, adds, health, resistances, and death/respawn chains remain active.
    /// </summary>
    public static class EpicEncounterMechanics
    {
        public static bool AllowDisplacement => false;
        public static bool AllowPositionPuzzle => false;
        public static bool AllowInvulnerableHazards => false;
        public static bool AllowAddShield => false;
        // Torst/Hurika keep their original database perches, attacks and loot.
        // Their unsupported aerial patrol/pull puzzle is separate from dragons'
        // existing fly/land cycle, which is deliberately not affected here.
        public static bool AllowGlacierGriffonPatrol => false;

        // Original OpenDAoC mechanics the fork had edited out for bot raids (docs/encounters/EPIC-ENCOUNTERS.md, "Restored
        // behind switches"). Each is off, so the game plays as before, until the raid AI can play it. The values were read
        // from OpenDAoC-Core 3513bad (2026-11-04). Settable so the tests and the next raid AI step can turn one on.

        /// <summary>Fames, Morbus and Funus heal to full when drawn past their tether (not only when the fight is over).</summary>
        public static bool AllowHorsemenTetherHeal { get; set; }
        /// <summary>Funus takes damage only from clerics and from mercenaries, armsmen, infiltrators and scouts with their
        /// listed weapon; any other attacker (pets included) heals him to full.</summary>
        public static bool AllowFunusWeaponPuzzle { get; set; }
        /// <summary>Skeletal Sacristan walks his loop at 220 without stopping, with 70% magic resists.</summary>
        public static bool AllowSacristanPatrol { get; set; }
        /// <summary>Soul Reckoner takes no damage at all while a reckoned soul lives (not a quarter).</summary>
        public static bool AllowSoulReckonerFullShield { get; set; }
        /// <summary>Soul Reckoner's room is 900 units from its middle (not 1,500).</summary>
        public static bool AllowSoulReckonerSmallRoom { get; set; }
        /// <summary>Aroon takes only the damage types whose Scaith has died, and the Scaiths must die in order (slash, thrust,
        /// crush, body, cold, energy, heat, matter, spirit); each Scaith is immune until the one before it is dead.</summary>
        public static bool AllowAroonDamageOrder { get; set; }
        /// <summary>Galladoria's primals come only when a raider stands within 350 of the waterfall bridge spot (bots too;
        /// the original counted real players only, which a bot raid could never meet).</summary>
        public static bool AllowGalladoriaBridgeSpot { get; set; }
        /// <summary>The Fire primal's trail of fire hits 220 in a 450 radius (not 120 in 300).</summary>
        public static bool AllowFullFirePatches { get; set; }
        /// <summary>Each Earthmender takes damage only from its own class group (pets always); others do nothing.</summary>
        public static bool AllowEarthmenderClassPuzzle { get; set; }
        /// <summary>Earthmenders never attack (they only heal).</summary>
        public static bool AllowEarthmendersPassive { get; set; }
        /// <summary>An Earthmender heals to full 30 s after its last fight even with raiders in the room.</summary>
        public static bool AllowEarthmenderQuickReset { get; set; }
        /// <summary>Earthmenders heal each other as well as the Earth primal.</summary>
        public static bool AllowEarthmenderMutualHeal { get; set; }
        /// <summary>Beatha's damage heals Xaga by twice the amount.</summary>
        public static bool AllowBeathaHealsXaga { get; set; }
        /// <summary>Beatha and Tine circle the room even while they are fought.</summary>
        public static bool AllowFairyFlight { get; set; }
        /// <summary>Grand Summoner Govannon has 300,000 health (not 200,000).</summary>
        public static bool AllowGovannonFullHealth { get; set; }
        /// <summary>Cold damage heals Icelord Kvasir by a fifth of his health and does no damage.</summary>
        public static bool AllowKvasirColdHeal { get; set; }
        /// <summary>Damage to King Tuscar heals Queen Kula, and damage to her heals him, while the other is below full health.</summary>
        public static bool AllowTuscarTwinHeal { get; set; }

        /// <summary>
        /// True 1.65 edition: the restore switches the epic_original_mechanics setting names ("all" for every one), read at
        /// server start. Unknown names are logged and ignored. Returns the switches turned on.
        /// </summary>
        public static string[] ApplySetting(string setting) => ApplySetting(setting, out _);

        public static string[] ApplySetting(string setting, out string[] unknown)
        {
            var on = new System.Collections.Generic.List<string>();
            var wanted = new System.Collections.Generic.HashSet<string>(
                (setting ?? string.Empty).Split(new[] { ',', ';', ' ' }, System.StringSplitOptions.RemoveEmptyEntries), System.StringComparer.OrdinalIgnoreCase);
            bool all = wanted.Contains("all");
            foreach (string name in RestoreSwitches)
            {
                bool value = all || wanted.Remove(name);
                typeof(EpicEncounterMechanics).GetProperty(name)!.SetValue(null, value);
                if (value) on.Add(name);
            }
            wanted.Remove("all");
            unknown = System.Linq.Enumerable.ToArray(wanted);
            return on.ToArray();
        }

        private static readonly DOL.Logging.Logger Log = DOL.Logging.LoggerManager.Create(typeof(EpicEncounterMechanics));

        [DOL.Events.GameServerStartedEvent]
        public static void OnServerStarted(DOL.Events.DOLEvent e, object sender, System.EventArgs args)
        {
            string[] on = ApplySetting(DOL.GS.ServerProperties.Properties.EPIC_ORIGINAL_MECHANICS, out string[] unknown);
            if (unknown.Length > 0)
                Log.Warn($"epic_original_mechanics: unknown switch {string.Join(", ", unknown)} (ignored)");
            if (on.Length > 0)
                Log.Info($"EPIC_ORIGINAL_MECHANICS on: {string.Join(", ", on)}");
        }

        /// <summary>Every restore switch, by name (for logs, tests and the catalog).</summary>
        public static readonly string[] RestoreSwitches =
        [
            nameof(AllowHorsemenTetherHeal), nameof(AllowFunusWeaponPuzzle), nameof(AllowSacristanPatrol),
            nameof(AllowSoulReckonerFullShield), nameof(AllowSoulReckonerSmallRoom), nameof(AllowAroonDamageOrder),
            nameof(AllowGalladoriaBridgeSpot), nameof(AllowFullFirePatches), nameof(AllowEarthmenderClassPuzzle),
            nameof(AllowEarthmendersPassive), nameof(AllowEarthmenderQuickReset), nameof(AllowEarthmenderMutualHeal),
            nameof(AllowBeathaHealsXaga), nameof(AllowFairyFlight), nameof(AllowGovannonFullHealth), nameof(AllowKvasirColdHeal),
            nameof(AllowTuscarTwinHeal),
        ];

        /// <summary>
        /// The scripted bosses of the epic dungeons, Summoner's Hall and Darkness Falls reset (walk home, full health,
        /// adds and phases cleared) on any brain turn with an empty aggro list. In a raid of 200 the list can be empty
        /// for a moment while the boss is still being hit, and Hurionthex in Galladoria went from 31% back to full
        /// that way, again and again (2026-10-14). The reset now waits until the boss has been out of combat for
        /// <see cref="ResetQuietMilliseconds"/>; a wiped or departed raid still resets it as before.
        /// </summary>
        public const int ResetQuietMilliseconds = 8_000;

        /// <summary>
        /// Princes Ba'alorien and Asmoien in Darkness Falls devour pets near them and heal by the pet's health. They ate
        /// every pet within 2,000 units on every brain turn, so a raid with pet classes could never wound them
        /// (Asmoien skipped at 100% with raiders beside him, 2026-10-15). Mechanic change (owner, 2026-10-14: change
        /// mechanics rather than health): one pet every five seconds, the nearest; the heal per pet is unchanged.
        /// </summary>
        public const int PetDevourIntervalMilliseconds = 5_000;

        /// <summary>
        /// What the princes may devour: a living pet. A gamebot's own brain also implements IControlledBrain (it can
        /// follow a player), and a gamebot is an NPC to the server, so the old test ate the raiders themselves, one every
        /// five seconds, usually the tank (Darkness Falls audit L1, 2026-11-11). Gamebots are never prey; their pets are.
        /// </summary>
        public static bool IsDevourablePet(GameNPC candidate) =>
            candidate != null && IsDevourablePet(candidate is GameBot, candidate.Brain is DOL.AI.Brain.IControlledBrain, candidate.IsAlive);

        public static bool IsDevourablePet(bool isGameBot, bool hasControlledBrain, bool isAlive) => isAlive && hasControlledBrain && !isGameBot;

        /// <summary>
        /// Who a boss's call for help reaches: its own kind, a living monster of its realm that is nobody's pet. High Lord
        /// Baln's first hit ordered every idle NPC within 3,000 to attack, so a raid's own gamebots and pets were told to
        /// turn on the raider who struck him (Darkness Falls audit M1, 2026-11-11).
        /// </summary>
        public static bool IsOwnKind(GameNPC boss, GameNPC mob) =>
            boss != null && mob != null && mob != boss &&
            IsOwnKind(mob.Realm == boss.Realm, mob is GameBot, mob.Brain is DOL.AI.Brain.IControlledBrain, mob.IsAlive,
                (mob.Flags & GameNPC.eFlags.PEACE) != 0);

        public static bool IsOwnKind(bool sameRealm, bool isGameBot, bool hasControlledBrain, bool isAlive, bool isPeaceful) =>
            sameRealm && isAlive && !isGameBot && !hasControlledBrain && !isPeaceful;

        /// <summary>
        /// Encounters of several bodies that share one health pool (Olcasgean and his second body, the Giant Sporite
        /// Cluster and its copies) die together, but each body paid out its own loot and experience to whoever had hit
        /// that body, so a raid that hit two bodies was paid twice (epic-bosses M5). The body that pays takes over the
        /// others' attackers with this before it dies; the others are left with none and pay nothing.
        /// </summary>
        public static void TransferDamageCredit(GameLiving from, GameLiving into)
        {
            if (from == null || into == null || from == into)
                return;
            System.Collections.Generic.Dictionary<GameLiving, double> taken = new();
            lock (from.XpGainersLock)
            {
                MergeDamageCredit(from.XPGainers, taken);
                from.XPGainers.Clear();
            }
            lock (into.XpGainersLock)
                MergeDamageCredit(taken, into.XPGainers);
        }

        public static void MergeDamageCredit<T>(System.Collections.Generic.IDictionary<T, double> from, System.Collections.Generic.IDictionary<T, double> into)
        {
            foreach (var pair in from)
                into[pair.Key] = into.TryGetValue(pair.Key, out double already) ? already + pair.Value : pair.Value;
        }

        /// <summary>
        /// The size of the force a boss kill notice reports: the real players and the gamebots around the body. The notices
        /// counted real players only, so a bot raid's kill read "slain by a force of 0 warriors" (dragon-fight M4).
        /// </summary>
        public static int ForceSize(GameNPC boss)
        {
            if (boss == null)
                return 0;
            int count = boss.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE).Count;
            foreach (GameNPC npc in boss.GetNPCsInRadius(WorldMgr.VISIBILITY_DISTANCE))
                if (npc is GameBot)
                    count++;
            return count;
        }

        public static bool FightIsOver(GameNPC boss) => boss == null || !boss.IsAlive || !boss.InCombatInLast(ResetQuietMilliseconds);
    }
}
