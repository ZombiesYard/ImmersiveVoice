using ImmersiveVoice;
#if !PORTABLE_TESTS
using Mono.Cecil;
#endif

int assertions = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    assertions++;
    Console.WriteLine("PASS: " + name);
}
VoiceCue Cue(string key = "loot:a", string group = "loot", bool urgent = false, int phrase = 96)
    => new() { Key = key, Group = group, Urgent = urgent, Phrase = phrase, Repeat = 120, GroupGap = 30 };

var p = new VoicePolicy();
var loot = Cue();
p.Observe(loot, 10, 1);
Check(!p.CanUseContext(10.9f), "Y requires a stable reaction window");
Check(p.ContextPending(10.9f), "fresh Y cue takes priority over ambient speech");
p.Observe(loot, 10.9f, 4);
Check(p.CanUseContext(11), "repeated observations do not postpone the reaction");
p.Observe(Cue("loot:b"), 11, 1);
Check(!p.CanUseContext(11.5f), "looking at a different object restarts reaction");
p.Observe(null, 11.5f, 0);
Check(!p.CanUseContext(15), "an expired/hidden Y prompt cannot be spoken later");
p.Observe(loot, 15, 0);
p.Attempt(15);
Check(!p.CanUseContext(16.9f), "failed native playback cannot cause a polling-rate retry loop");
Check(p.CanUseContext(17), "failed playback can retry after backoff");
p.Spoken(17, 3, 8, true, false, 12, loot);
Check(!p.CanSpeak(19.9f, true), "urgent cue does not interrupt an existing phrase");
Check(p.CanSpeak(20.5f, true), "urgent cue can bypass the ordinary pause after playback");
Check(!p.CanSpeak(27.9f), "normal cooldown starts at the end of the clip");
Check(p.CanSpeak(28), "normal cooldown releases at its deadline");
p.Observe(Cue("loot:b"), 40, 0);
Check(!p.CanUseContext(40), "different objects share category anti-spam cooldown");
Check(p.CanUseContext(47), "a new object is allowed after category cooldown");
p.Observe(loot, 100, 0);
Check(!p.CanUseContext(100), "same object stays deduplicated after global cooldown");
p.Observe(Cue("loot:a", phrase: 103), 100, 0);
Check(!p.CanUseContext(100), "another native phrase about the same body remains deduplicated");
p.Observe(loot, 137, 0);
Check(p.CanUseContext(137), "persistent contexts can repeat after their long cooldown");

p = new VoicePolicy();
var grenade = Cue("grenade", "grenade", true, 11);
p.Observe(grenade, 10, 0);
p.Manual(10, 12);
Check(!p.CanUseContext(21.99f), "manual input suppresses even urgent automatic voice");
Check(p.CanUseContext(22), "manual pause eventually expires");
p.Spoken(22, 4, 8, false, false, 12, null);
Check(!p.CanUseContext(37.9f), "manual playback extends pause through the whole clip");
Check(p.CanUseContext(38), "automatic voice resumes after manual clip and pause");
p = new VoicePolicy();
p.Observe(grenade, 10, 0);
p.Spoken(10, 2, 8, false, true, 12, null);
Check(!p.CanUseContext(11), "pain sounds are never interrupted");
Check(p.CanUseContext(12.5f), "pain sounds do not impose a long manual voice cooldown");

p = new VoicePolicy();
p.Observe(loot, 10, 1);
p.Observe(Cue("loot:a", phrase: 103), 10.5f, 1);
Check(!p.CanUseContext(11.1f), "native priority change restarts the reaction window");
Check(p.CanUseContext(11.5f), "changed priority eventually becomes ready");
Check(!p.ContextPending(14), "unplayable native banks cannot starve ambient voice forever");
p.Spoken(14, 2, 8, false, false, 12, loot);
Check(!p.CanUseContext(60), "manually spoken context also counts for deduplication");
Check(new VoicePolicy().CanSpeak(60), "new raid has no previous raid cooldown state");

Reaction R(string group, int priority, float ready = 10, float expires = 20) => new()
{ Cue = Cue(group + ":a", group, priority >= 82), Priority = priority, ReadyAt = ready, ExpiresAt = expires };
var q = new ReactionQueue();
var reload = R("reload", 97);
q.Offer(R("contact", 76), 9); q.Offer(reload, 9); q.Offer(R("heal", 82), 9);
Check(q.Next(10, _ => true, _ => true) == reload, "reload outranks contact and healing, not lost behind chatter");
q.Offer(R("reload", 97, 15, 60), 10);
Check(q.Count == 3 && q.Next(10, _ => true, _ => true) == reload && reload.ExpiresAt == 20,
    "duplicate callbacks cannot delay or extend a queued event");
var urgentGrenade = R("grenade", 100);
q.Offer(urgentGrenade, 10);
Check(q.Next(10, _ => true, _ => true) == urgentGrenade, "grenade warning outranks reload");
q.Remove(urgentGrenade);
Check(q.Next(10, x => x.Cue.Group != "reload", _ => true)?.Cue.Group == "heal",
    "finished reload is removed instead of spoken later");
Check(q.Next(21, _ => true, _ => true) == null && q.Count == 0, "expired event queue never backlogs");
q.Offer(R("health", 87), 9);
q.Offer(R("health", 94), 9);
Check(q.Count == 1 && q.Next(10, _ => true, _ => true)?.Priority == 94, "severe injury replaces mild injury with one response");
q.Clear();
for (int i = 0; i < 20; i++) q.Offer(R("group" + i, i), 9);
Check(q.Count == 12 && q.Next(10, _ => true, _ => true)?.Priority == 19, "queue remains bounded and keeps urgent events");
q.Clear();
q.Offer(R("reply", 45, 15, 20), 10);
Check(!q.Reserve(10) && q.Reserve(14.4f), "a delayed teammate reply reserves only its near reaction window");
p = new VoicePolicy();
p.Manual(10, 3);
Check(!p.CanUse(reload.Cue, 12) && p.CanUse(reload.Cue, 13), "event queue respects manual priority, including reload");

var memory = new ContactMemory();
Check(memory.Observe("neutral", true, false, 10) == null, "a visible neutral alone is not declared hostile");
memory.Hostile("ally", 10);
Check(memory.Observe("ally", true, true, 11) == null, "same-group players do not produce enemy contact");
memory.Hostile("unknown-dead", 10);
Check(memory.Observe("unknown-dead", false, false, 11) == null, "unseen corpse is never a confirmed kill");
memory.Hostile("enemy", 10);
Check(memory.Observe("enemy", true, false, 10) == null, "contact requires stable visual confirmation");
Check(memory.Observe("enemy", true, false, 10.3f) == ReactionKind.Contact, "confirmed visible hostile can trigger contact");
Check(memory.Lost(13, true) == null && memory.Lost(14.4f, true) == "enemy", "lost visual requires four seconds without sight");
Check(memory.Lost(14.5f, true) == null && memory.LostStillValid("enemy", 14.5f), "lost visual is emitted once per loss");
memory.Observe("enemy", true, false, 15);
Check(memory.Observe("enemy", true, false, 15.3f) == ReactionKind.RepeatedContact && !memory.LostStillValid("enemy", 15.3f),
    "reacquisition has its own meaning and cancels stale lost-visual commentary");
Check(memory.Observe("enemy", false, false, 16) == ReactionKind.EnemyDown && memory.DeathConfirmed("enemy"),
    "a recently seen hostile's visible death can trigger one ending");
Check(memory.Observe("enemy", false, false, 17) == null && memory.Lost(21, true) == null,
    "dead target cannot repeat its ending or trigger lost visual");
memory.Clear();
memory.Hostile("old", 10); memory.Observe("old", true, false, 10);
Check(memory.Observe("old", false, false, 21) == null, "old sightings cannot justify a kill report");
memory.Clear();
memory.Hostile("first", 1); memory.Observe("first", true, false, 1);
for (int i = 0; i < 40; i++) { memory.Hostile("bounded" + i, 2 + i); memory.Observe("bounded" + i, true, false, 2 + i); }
Check(!memory.IsHostile("first", 45) && memory.IsHostile("bounded39", 45), "contact memory evicts old identities and stays bounded");
memory.Clear();
Check(!memory.RecentLivingThreat(45, 8), "suspend/raid reset forgets prior threats");

var fight = new CombatMemory();
fight.Shot(10, false, false);
Check(!fight.Active(10), "test firing and native Awareness alone do not authorize battle trash talk");
fight.Shot(11, true, false);
Check(fight.Active(15) && !fight.Active(19), "a real aimed exchange authorizes only eight seconds of battle chatter");
fight.Incoming(20); fight.Shot(26, false, false);
Check(fight.Active(30), "returning fire shortly after enemy damage renews the fight");
fight.Defeat(31, false);
Check(!fight.Active(31) && fight.Settling(31), "confirmed last observed death ends periodic taunts and leaves a quiet recovery window");
fight.Shot(32, false, false);
Check(!fight.Active(32), "firing into a corpse cannot renew combat from an earlier incoming hit");
fight.Shot(33, true, false); fight.Defeat(34, true);
Check(fight.Active(35), "killing one target does not suppress ongoing exchange with another visible threat");
Check(!fight.Active(42) && !fight.Settling(50), "both battle chatter and recovery memory expire naturally");
fight.Incoming(60); fight.Clear();
Check(!fight.Active(60) && !fight.Settling(60), "suspending voice clears combat evidence");

// Product scenarios: survival interrupts a delayed celebration; nearby Fika
// teammates receive information; different clips still repeat the same meaning.
var context = new BarkContext { Mode = VoiceMode.Solo, Combat = CombatPhase.Recovering, FightDuration = 8f };
Check(SemanticVoice.EnemyDown(context, true) == VoiceIntent.KillTaunt
    && SemanticVoice.EnemyDown(context, false) == VoiceIntent.KillConfirm,
    "healthy Solo retains the taunt roll and confirmation after a sustained exchange");
foreach (var mode in new[] { VoiceMode.FikaHost, VoiceMode.FikaClient })
{
    context.Mode = mode; context.NearbyTeammate = true;
    Check(SemanticVoice.EnemyDown(context, true) == VoiceIntent.TacticalEnemyDown,
        mode + " gives nearby teammates a report even when the emotional roll succeeds");
    context.NearbyTeammate = false;
    Check(SemanticVoice.EnemyDown(context, true) == VoiceIntent.KillTaunt,
        mode + " alone does not fabricate a nearby audience");
}
context.Mode = VoiceMode.Solo; context.NearbyTeammate = true;
Check(SemanticVoice.EnemyDown(context, true) == VoiceIntent.KillTaunt,
    "a Solo friendly NPC does not select the Fika policy");
context.NearbyTeammate = false;
foreach (var injury in new[] { InjuryLevel.BadlyInjured, InjuryLevel.Dying })
{
    context.Injury = injury;
    Check(SemanticVoice.EnemyDown(context, true) == VoiceIntent.Silent
        && !SemanticVoice.StillValid(VoiceIntent.CombatBark, context, 0),
        injury + " suppresses kill celebrations and periodic combat emotion");
}
context.Injury = InjuryLevel.Healthy; context.Healing = true;
Check(!SemanticVoice.StillValid(VoiceIntent.KillTaunt, context, 0), "starting treatment cancels delayed celebration");
context.Healing = false; context.RecentlyHit = true;
Check(SemanticVoice.EnemyDown(context, false) == VoiceIntent.Silent, "fresh damage takes precedence over a kill report");
context.RecentlyHit = false; context.DamageVersion = 1;
Check(!SemanticVoice.StillValid(VoiceIntent.KillTaunt, context, 0),
    "new damage permanently invalidates the old taunt even after its immediate hit window ends");
context.DamageVersion = 0;
Check(SemanticVoice.StillValid(VoiceIntent.KillTaunt, context, 0), "unchanged healthy context preserves a valid taunt");
context.Mode = VoiceMode.FikaClient; context.NearbyTeammate = true;
Check(!SemanticVoice.StillValid(VoiceIntent.KillTaunt, context, 0),
    "a newly observed teammate cancels a stale Solo-style emotional choice");
Check(!SemanticVoice.StillValid(VoiceIntent.Silent, context, 0), "Silence can never authorize playback");

var damage = new DamageWindow();
for (int i = 0; i < 8; i++) damage.Add(10 + i * 0.05f, 8, i == 7 ? InjuryLevel.BadlyInjured : InjuryLevel.Injured, false);
Check(damage.Hits == 8 && damage.Damage == 64 && damage.Severity == InjuryLevel.BadlyInjured,
    "eight pellets aggregate into one window carrying the worst observed injury");
Check(!damage.Ready(10.79f) && damage.Ready(10.81f) && damage.StartedAt == 10,
    "later pellets never postpone the original 0.8-second response window");
damage.Clear();
Check(!damage.Ready(20) && damage.Hits == 0, "manual/suspend clears an unfinished damage burst");
damage.Add(20, 5, InjuryLevel.Healthy, true);
Check(damage.FriendlyOnly, "an isolated friendly hit can select FriendlyFire");
damage.Add(20.2f, 10, InjuryLevel.Injured, false);
Check(!damage.FriendlyOnly, "mixed enemy and friendly impacts do not blame the teammate for the whole burst");
damage.Clear(); damage.Add(25, 3, InjuryLevel.Healthy, false);
Check(damage.Severity == InjuryLevel.Healthy && damage.StartedAt == 25,
    "a later damage window does not inherit a previous burst's severity or time");

var tauntCue = Cue("kill:one", "kill"); tauntCue.Intent = VoiceIntent.KillTaunt; tauntCue.SemanticRepeat = 40;
var fightCue = Cue("combat", "combat"); fightCue.Intent = VoiceIntent.CombatBark; fightCue.SemanticRepeat = 40;
p = new VoicePolicy(); p.Spoken(10, 2, 8, true, false, 12, tauntCue);
Check(p.BlockReason(fightCue, 30) == "RecentSemanticIntent" && p.CanUse(fightCue, 50),
    "a kill taunt suppresses periodic combat speech through shared semantic history");
p = new VoicePolicy(); p.Spoken(10, 2, 8, true, false, 12, fightCue);
Check(p.BlockReason(tauntCue, 30) == "RecentSemanticIntent", "periodic combat speech also suppresses a later kill taunt");
Check(p.CanUse(grenade, 30), "emotional repetition never blocks a grenade warning");
p = new VoicePolicy(); p.Attempt(10);
Check(p.CanUse(fightCue, 12), "missing audio never consumes semantic history");
p.Spoken(20, 2, 8, false, false, 12, fightCue);
Check(p.BlockReason(tauntCue, 40) == "RecentSemanticIntent", "manual F1 participates in emotional repetition control");

var drops = new List<string>();
q = new ReactionQueue((_, why) => drops.Add(why));
var delayedTaunt = R("kill", 40, 11, 17); delayedTaunt.Cue.Intent = VoiceIntent.KillTaunt;
q.Offer(delayedTaunt, 10);
context = new BarkContext { Mode = VoiceMode.Solo, DamageVersion = 1 };
Check(q.Next(11, r => SemanticVoice.StillValid(r.Cue.Intent, context, r.DamageVersion), _ => true) == null
    && q.Count == 0 && drops.SequenceEqual(new[] { "ContextInvalid" }),
    "a delayed taunt invalidated by damage is removed, not replayed later");
q.Offer(R("reload", 97, 20, 21), 19); q.Next(22, _ => true, _ => true); q.Next(23, _ => true, _ => true);
Check(drops.Count(x => x == "Expired") == 1, "expiration telemetry is emitted once per dropped candidate");
q.Offer(R("kill", 40), 9); q.Offer(R("contact", 76), 9); q.Offer(urgentGrenade, 9);
Check(q.Next(10, _ => true, _ => true) == urgentGrenade, "tactical grenade wins over both contact and emotional celebration");

// Asset-driven contextual additions: action-level decisions, wounds, and brief
// encounter memory. Automatic fire/pellets must not multiply emotional weight.
fight = new CombatMemory();
fight.Shot(10, false, false);
Check(fight.PeakPressure(10) == 0 && fight.Duration(10) == 0,
    "test fire creates neither pressure nor an encounter duration");
for (int i = 0; i < 50; i++) fight.Shot(11 + i * 0.01f, true, false);
Check(fight.PeakPressure(12) == 1f && fight.Duration(12) < 1f,
    "automatic fire counts at most one pressure increment per second");
for (int i = 0; i < 20; i++) fight.Incoming(12 + i * 0.01f);
Check(fight.PeakPressure(13) < 4f, "one pellet burst cannot manufacture a high-pressure fight");
fight.Incoming(13); fight.Shot(14, true, false);
Check(fight.PeakPressure(14) >= 4f && fight.Duration(14) == 3f,
    "repeated exchanges and separate incoming bursts accumulate encounter pressure");
fight.Defeat(14.5f, false);
Check(!fight.Active(15) && fight.PeakPressure(15) >= 4f && fight.Duration(15) == 3f,
    "ending combat preserves its experience without counting the quiet wait as fighting");
Check(fight.PeakPressure(30) == 0 && fight.Duration(30) == 0,
    "pressure and duration expire instead of becoming permanent personality state");
fight.Defeat(31, false); fight.Shot(32, true, false);
Check(fight.Duration(32) == 0 && fight.PeakPressure(32) == 1,
    "late death observation cannot attach a new fight to ancient experience");
fight.Clear();
Check(fight.Duration(32) == 0 && fight.PeakPressure(32) == 0, "suspension also resets encounter intensity");

context = new BarkContext { Combat = CombatPhase.Calm };
Check(SemanticVoice.Reload(context, 0.499) && !SemanticVoice.Reload(context, 0.5),
    "calm top-ups default to a single fifty-percent action decision");
Check(!SemanticVoice.Reload(context, 0, calmPercent: 0), "zero calm chance suppresses only calm reload decisions");
Check(SemanticVoice.Reload(context, 0.999999, calmPercent: 100), "one hundred percent calm chance admits every valid probability roll");
Check(SemanticVoice.Reload(context, 0.199, calmPercent: 20) && !SemanticVoice.Reload(context, 0.2, calmPercent: 20),
    "custom calm reload probability is used at the exact configured boundary");
Check(SemanticVoice.ReloadPriorityFor(context) < 82, "calm reloads respect normal spacing and stealth");
context.Combat = CombatPhase.Engaged;
Check(SemanticVoice.Reload(context, 0.999) && SemanticVoice.ReloadPriorityFor(context) == SemanticVoice.ReloadPriority,
    "active combat preserves the established reload behavior without a chance veto");
Check(SemanticVoice.Reload(context, 0.999, calmPercent: 0, recoveryPercent: 0),
    "non-combat probability settings cannot suppress an active combat reload");
context.Combat = CombatPhase.Recovering;
Check(SemanticVoice.Reload(context, 0.649) && !SemanticVoice.Reload(context, 0.65),
    "ordinary recovery reloads default to sixty-five percent without guaranteeing speech");
context.PeakPressure = 4;
Check(SemanticVoice.Reload(context, 0.5) && !SemanticVoice.Reload(context, 0.8),
    "a hard exchange makes a recovery reload more likely without guaranteeing speech");
Check(SemanticVoice.ReloadChance(context, recoveryPercent: 10) == 75f
    && SemanticVoice.ReloadChance(context, recoveryPercent: 100) == 100f,
    "intense recovery retains its seventy-five percent floor and honors higher settings");
var reloadDecision = new ReloadDecision();
reloadDecision.Native(10);
Check(reloadDecision.Begin() && !reloadDecision.Begin(), "duplicate reload hints receive only one decision");
reloadDecision.Update(10.2f, true); reloadDecision.Native(10.3f);
Check(!reloadDecision.Begin() && reloadDecision.Valid(12),
    "the operation edge and later hints cannot reroll an earlier silent reload");
reloadDecision.Update(13, false);
Check(!reloadDecision.Valid(13), "completed reload invalidates its pending line");
reloadDecision.Update(14, true);
Check(reloadDecision.Begin(), "the next real reload receives a fresh decision");
reloadDecision.Spoken();
Check(!reloadDecision.Begin(), "manual or native reload speech prevents a second automatic request");
reloadDecision.Clear(); reloadDecision.Native(20); reloadDecision.Begin();
Check(!reloadDecision.Valid(21.01f), "a native hint without a continuing action expires");
reloadDecision.Native(22);
Check(reloadDecision.Begin(), "an expired native hint cannot suppress future actions");

context = new BarkContext { Injury = InjuryLevel.Injured, Bleeding = true, ArmFracture = true };
Check(SemanticVoice.Injury(context, false) == VoiceIntent.Bleeding, "active bleeding outranks a fracture in the same injury response");
context.Bleeding = false;
Check(SemanticVoice.Injury(context, false) == VoiceIntent.ArmFracture, "a confirmed arm fracture selects the original arm line");
context.LegFracture = true;
Check(SemanticVoice.Injury(context, false) == VoiceIntent.LegFracture, "multiple fractures still choose one response");
context.HeavyBleeding = true; context.Injury = InjuryLevel.BadlyInjured;
Check(SemanticVoice.Injury(context, true) == VoiceIntent.Bleeding,
    "heavy bleeding reports the urgent condition even after friendly fire");
Check(SemanticVoice.EnemyDown(context, true) == VoiceIntent.Silent,
    "a heavily bleeding survivor does not celebrate an observed down");
context.Injury = InjuryLevel.Healthy;
Check(!SemanticVoice.StillValid(VoiceIntent.CombatBark, context, 0),
    "fresh heavy bleeding suppresses taunts before the overall health tag falls");
context.Injury = InjuryLevel.Dying;
Check(SemanticVoice.Injury(context, true) == VoiceIntent.Injury, "near death takes precedence over wound details and blame");
context = new BarkContext();
Check(SemanticVoice.Injury(context, true) == VoiceIntent.FriendlyFire,
    "an otherwise healthy friendly hit preserves the friendly-fire reaction");
context.Combat = CombatPhase.Recovering;
Check(!SemanticVoice.Recovery(context), "one easy encounter does not require a closing mutter");
context.FightDuration = 8;
Check(SemanticVoice.Recovery(context), "a sustained exchange can produce one recovery reaction");
context.FightDuration = 0; context.PeakPressure = 4;
Check(SemanticVoice.Recovery(context), "a short but intense exchange also supports recovery");
context.PeakPressure = 0; context.Bleeding = true;
Check(SemanticVoice.Recovery(context), "a wounded survivor can report the remaining condition after a brief exchange");
context.Healing = true;
Check(!SemanticVoice.Recovery(context), "ongoing treatment cancels recovery commentary");
context.Healing = false; context.RecentlyHit = true;
Check(!SemanticVoice.Recovery(context), "fresh damage must settle before recovery commentary");
context.RecentlyHit = false; context.Combat = CombatPhase.Engaged;
Check(!SemanticVoice.Recovery(context), "resumed combat invalidates a delayed recovery reaction");

q = new ReactionQueue();
q.Offer(R("reload", SemanticVoice.ReloadPriority), 9);
var critical = R("health", SemanticVoice.InjuryPriority(InjuryLevel.Dying)); q.Offer(critical, 9);
Check(q.Next(10, _ => true, _ => true) == critical, "near-death response outranks a combat reload");
var danger = R("grenade-warning", SemanticVoice.GrenadeWarningPriority); q.Offer(danger, 9);
q.Offer(R("grenade-out", SemanticVoice.GrenadeOutPriority), 9);
Check(q.Count == 4 && q.Next(10, _ => true, _ => true) == danger,
    "an own-throw candidate cannot deduplicate away a higher-priority grenade warning");
Check(SemanticVoice.InjuryPriority(InjuryLevel.Healthy, true) > SemanticVoice.ReloadPriority,
    "heavy bleeding also outranks a combat reload");
p = new VoicePolicy();
var throwCue = Cue("out", "grenade-out", true); var dangerCue = Cue("danger", "grenade-warning", true);
p.Spoken(10, 0.5f, 8, true, false, 12, throwCue);
Check(p.CanUse(dangerCue, 11), "own-throw repetition history cannot mute an enemy grenade warning");
p.Spoken(20, 1, 8, true, false, 12, Cue("health:bleed", "health", true));
Check(!p.CanUse(Cue("health:fracture", "health", true), 35),
    "wound details and later recovery reports share one health category cooldown");

EncounterScenarios.Run(Check);
MedicalScenarios.Run(Check);
TreatmentScenarios.Run(Check);
HealthSpeechScenarios.Run(Check);
PlaybackScenarios.Run(Check);
TacticalReliabilityScenarios.Run(Check);
RaidRegressionScenarios.Run(Check);
EnemyDownWaitScenarios.Run(Check);
SquadScenarios.Run(Check);
SquadTacticalScenarios.Run(Check);
SquadContinuityScenarios.Run(Check);
JournalScenarios.Run(Check);
NpcReplyScenarios.Run(Check);

// Exhaust every ownership/lifecycle combination; only a live, running, local
// human outside the hideout/headless/downed/extracted states may speak.
int accepted = 0;
for (int bits = 0; bits < 1024; bits++)
{
    bool B(int n) => (bits & (1 << n)) != 0;
    bool actual = RuntimeRules.IsLocalRaid(B(0), B(1), B(2), B(3), B(4), B(5), B(6), B(7), B(8), B(9));
    bool expected = !B(0) && !B(1) && B(2) && !B(3) && B(4) && B(5) && B(6) && !B(7) && B(8) && !B(9);
    if (actual != expected) throw new Exception("Ownership gate mismatch: " + bits);
    if (actual) accepted++;
}
Check(accepted == 1, "1024 lifecycle/ownership combinations: no AI, remote, headless, hideout, downed, dead, extracted or stopped voices");

#if PORTABLE_TESTS
Console.WriteLine($"All {assertions} portable rule checks passed, including the 1024 ownership/lifecycle combinations. Game binary interfaces were not tested.");
#else
if (args.Length < 2) throw new Exception("Pass the game root and built plugin DLL to validate real binaries.");
string root = Path.GetFullPath(args[0]);
var resolver = new DefaultAssemblyResolver();
foreach (string dir in new[] { "EscapeFromTarkov_Data/Managed", "BepInEx/core", "BepInEx/plugins/Fika" })
    resolver.AddSearchDirectory(Path.Combine(root, dir));
var reader = new ReaderParameters { AssemblyResolver = resolver };
using var game = AssemblyDefinition.ReadAssembly(Path.Combine(root, "EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll"), reader);
using var fika = AssemblyDefinition.ReadAssembly(Path.Combine(root, "BepInEx/plugins/Fika/Fika.Core.dll"), reader);
using var plugin = AssemblyDefinition.ReadAssembly(args[1], reader);
TypeDefinition Type(AssemblyDefinition a, string name) => a.MainModule.Types.Single(t => t.FullName == name);
MethodDefinition Method(AssemblyDefinition a, string type, string name) => Type(a, type).Methods.Single(m => m.Name == name);
bool Calls(MethodDefinition m, string needle) => m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.FullName.Contains(needle));
IEnumerable<TypeDefinition> Walk(IEnumerable<TypeDefinition> ts)
{
    foreach (var t in ts) { yield return t; foreach (var n in Walk(t.NestedTypes)) yield return n; }
}
Check(!plugin.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("Fika")), "SPT-only installs need no Fika assembly to load this plugin");
Check(!plugin.MainModule.AssemblyReferences.Any(r => r.Name == "ConfigurationManager"),
    "translated settings do not require a Configuration Manager assembly to load the voice plugin");
Check(Calls(Method(plugin, "ImmersiveVoice.Plugin", "LateUpdate"), "Settings::ApplyPendingMenu")
    && Calls(Method(plugin, "ImmersiveVoice.Plugin", "LateUpdate"), "ConfigurationMenu::Rebuild")
    && !Calls(Method(plugin, "ImmersiveVoice.Plugin", "LateUpdate"), "Plugin::FailSession")
    && Calls(Method(plugin, "ImmersiveVoice.Plugin", "OnDestroy"), "Settings::Dispose"),
    "menu updates are deferred outside GUI drawing, isolated from voice failures and detached on plugin destruction");
Check(Method(game, "EFT.UI.Gestures.GesturesQuickPanel", "Show").Parameters[0].ParameterType.FullName == "EFT.GamePlayerOwner", "native Show patch matches installed game signature");
Check(Method(game, "EFT.GamePlayerOwner", "QuickMumbleHandler").Parameters.Count == 0, "manual Y patch target exists");
Check(Method(game, "EFT.GamePlayerOwner", "MakeBindActionHandler").Parameters.Count == 2, "manual F1 bind patch target exists");
var say = Method(game, "EFT.Player", "Say");
Check(say.Parameters.Count == 6 && Calls(say, "EFT.BaseSpeaker::Play"), "Player.Say still reaches the native speaker");
Check(Calls(Method(game, "EFT.GamePlayerOwner", "QuickMumbleStart"), "GesturesQuickPanel::ActivateCommand")
    && Calls(Method(game, "EFT.GamePlayerOwner", "QuickMumbleStart"), "EFT.Player::Say"), "Y adapter retains native priority activation and playback");
Check(Calls(Method(fika, "Fika.Core.Main.Players.FikaPlayer", "OnPhraseTold"), "PhrasePacket::FromValue")
    && Calls(Method(fika, "Fika.Core.Main.Players.FikaPlayer", "OnPhraseTold"), "SendNetReusable"), "installed Fika sends native voice clips through its own network channel");
Check(Calls(Method(fika, "Fika.Core.Networking.Packets.Player.Common.SubPackets.PhrasePacket", "Execute"), "EFT.BaseSpeaker::PlayDirect"), "installed Fika receiver plays the transmitted native clip directly");
var observedSpeech = Method(fika, "Fika.Core.Main.Players.ObservedPlayer", "OnPhraseTold");
Check(Calls(observedSpeech, "EFT.Player::PlayPhraseClip") && !Calls(observedSpeech, "SendNetReusable")
    && !Calls(observedSpeech, "FikaPlayer::OnPhraseTold"), "observed remote speech plays locally without re-sending the phrase packet");
var phraseAudio = Method(game, "EFT.Player", "PlayPhraseClip");
Check(!phraseAudio.IsVirtual && phraseAudio.Parameters.Count == 1
    && phraseAudio.Parameters[0].ParameterType.Name == "TaggedClip"
    && Calls(phraseAudio, "EFT.Player::PlaySpeechFromTime")
    && Calls(Method(game, "EFT.Player", "OnPhraseTold"), "EFT.Player::PlayPhraseClip"),
    "NPC observation uses the actual audio endpoint shared by native and Fika received speech");
Check(Type(game, "EFT.Player").Fields.Any(f => f.Name == "_speechSource" && f.FieldType.Name == "BetterSource")
    && Type(game, "BetterSource").Properties.Any(p => p.Name == "OcclusionVolumeFactor")
    && Type(game, "BetterSource").Properties.Any(p => p.Name == "OcclusionRolloffScale"),
    "NPC audio observation has the installed source and native occlusion fields");
Check(!Type(plugin, "ImmersiveVoice.NpcReplyObserver").Methods.Where(m => m.HasBody).Any(m =>
    Calls(m, "RegisteredPlayers") || Calls(m, "Raycast") || Calls(m, "BotOwner")),
    "NPC replies do not scan the raid, cast rays or consult NPC brains");
Check(Calls(Method(plugin, "ImmersiveVoice.VoiceSession", ".ctor"), "SpeechObservation::add_Played")
    && Calls(Method(plugin, "ImmersiveVoice.VoiceSession", "Dispose"), "SpeechObservation::remove_Played"),
    "the local raid session owns and removes the speech observation listener");
Check(Calls(observedSpeech, "FikaBackendUtils::get_IsHeadless"), "observed speech retains Fika's headless audio guard");
Check(Calls(Method(fika, "Fika.Core.Main.Players.FikaPlayer", "ApplyDamageInfo"), "EFT.Player::ApplyDamageInfo"),
    "installed Fika damage path retains the original local hit event");
Check(Type(game, "EFT.Player").Events.Any(e => e.Name == "BeingHitAction")
    && Type(game, "EFT.Player").Events.Any(e => e.Name == "PhraseSituation"), "native damage and situation event hooks exist");
var shotReaction = Method(game, "EFT.Player", "ShotReactions");
Check(shotReaction.Parameters.Count == 2 && shotReaction.Parameters[0].ParameterType.FullName == "EFT.Ballistics.DamageInfo"
    && Calls(Method(fika, "Fika.Core.Main.Players.ObservedPlayer", "ShotReactions"), "EFT.Player::ShotReactions"),
    "the squad impact patch matches the native method actually called by observed Fika players");
Check(Type(game, "EFT.GameWorld").Events.Any(e => e.Name == "OnPersonAdd")
    && Calls(Method(game, "EFT.GameWorld", "GetAlivePlayerByProfileID"), "TryGetValue"),
    "squad discovery has a native registration event and constant-time identity lookup");
Check(!Calls(Method(plugin, "ImmersiveVoice.FikaBridge", "Downed"), "PropertyInfo::GetValue")
    && Calls(Method(plugin, "ImmersiveVoice.FikaBridge", "Downed"), "Func`2"),
    "frequent teammate downed checks use a cached delegate without reflection boxing");
Check(Calls(Method(plugin, "ImmersiveVoice.ViewProbe", "TeamVisible"), "Physics::RaycastNonAlloc")
    && !Method(plugin, "ImmersiveVoice.ViewProbe", "TeamVisible").Body.Instructions.Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Newarr),
    "squad sight probes reuse the fixed non-allocating hit buffer");
Check(Method(plugin, "ImmersiveVoice.ViewProbe", ".ctor").Body.Instructions.Any(i =>
        i.Operand is FieldReference f && f.FullName == "System.Int32 LayersMaskController::DeadbodyLayer")
    && Type(game, "EFT.Interactive.Corpse").Methods.Any(m => m.Name == "Init" && m.HasBody
        && m.Body.Instructions.Any(i => i.Operand is string layer && layer == "Deadbody")
        && Calls(m, "SetLayersRecursively")),
    "visual rays include the actual Deadbody layer used by native corpse creation");
Check(Method(plugin, "ImmersiveVoice.ViewProbe", "Track").Body.Instructions.Any(i =>
        i.Operand is FieldReference f && f.Name == "Anchor")
    && Calls(Method(plugin, "ImmersiveVoice.ViewProbe", "Track"), "ViewProbe::Trace"),
    "tracking has a surviving anchor fallback but still requires a real confirming ray");
Check(Method(plugin, "ImmersiveVoice.TargetTracker", "Track").Body.Instructions.Count(i =>
        i.Operand is MethodReference r && r.FullName.Contains("ViewProbe::Track")) == 1
    && Convert.ToInt32(Type(plugin, "ImmersiveVoice.TargetTracker").Fields.Single(f => f.Name == "Capacity").Constant) == 4,
    "recent enemy tracking is capped at four targets and one tracking probe call per vision tick");
Check(!Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "Valid"), "ViewProbe::")
    && Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "Valid"), "ContactMemory::ContactBlockReason"),
    "contact playback validates per-target observation memory without extra validation raycasts");
Check(Calls(Method(plugin, "ImmersiveVoice.SquadObserver", "Companion"), "SquadAwareness::Nearby")
    && Calls(Method(plugin, "ImmersiveVoice.SquadObserver", "Tick"), "SquadObserver::Companion")
    && Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "get_Context"), "SquadObserver::Nearby"),
    "behind-camera companion evidence feeds both motion sampling and tactical information context");
var trigger = Type(game, "EPhraseTrigger");
Check(Convert.ToInt32(trigger.Fields.Single(f => f.Name == "OnLostVisual").Constant) == 13
    && Convert.ToInt32(trigger.Fields.Single(f => f.Name == "LostVisual").Constant) == 77,
    "native visual-loss event and manual command retain distinct verified IDs");
var playNative = Method(game, "EFT.BaseSpeaker", "Play");
Check(playNative.ReturnType.FullName == "TagBank" && playNative.Parameters.Count == 4
    && playNative.Parameters[0].ParameterType.FullName == "EPhraseTrigger"
    && playNative.Parameters[1].ParameterType.FullName == "ETagStatus"
    && playNative.Parameters[3].Name == "importance"
    && playNative.Parameters[3].ParameterType.FullName == "System.Nullable`1<System.Int32>",
    "native playback hook arguments and nullable priority match the installed game");
Check(Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "UpdateMedicalRequest"), "VoiceDirector::MedicalRecipient")
    && Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "ValidMedicalRequest"), "VoiceDirector::MedicalRecipient")
    && Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "MedicalRecipient"), "MedicalRequestPolicy::Recipient"),
    "medical selection and playback both require a concrete recent human recipient");
var attemptScope = Method(plugin, "ImmersiveVoice.VoiceSession", "Attempt");
Check(Calls(attemptScope, "NativePlayback::Begin") && Calls(attemptScope, "NativePlayback::End")
    && attemptScope.Body.ExceptionHandlers.Any(h => h.HandlerType == Mono.Cecil.Cil.ExceptionHandlerType.Finally),
    "native compatibility scope is released even when an attempt throws");
var femalePath = Path.Combine(root, "BepInEx/plugins/FemalePMCSoundPatch.dll");
if (File.Exists(femalePath))
{
    using var female = AssemblyDefinition.ReadAssembly(femalePath, reader);
    var gate = Method(female, "FemalePMCSoundPatch.Plugin", "LocalSuppressTacticalCallouts");
    Check(gate.IsStatic && gate.ReturnType.FullName == "System.Boolean" && gate.Parameters.Count == 1
        && gate.Parameters[0].ParameterType.FullName == "EPhraseTrigger"
        && Calls(Method(female, "FemalePMCSoundPatch.NativeBreathDebouncePatch", "Prefix"), "LocalSuppressTacticalCallouts"),
        "installed optional female voice plugin has the precise gate used by its native-play prefix");
}
Check(new[] { ("GoForward", 33), ("Gogogo", 34), ("GetBack", 32), ("Regroup", 47), ("HoldPosition", 36), ("OnFriendlyDown", 14) }
    .All(pair => Convert.ToInt32(trigger.Fields.Single(f => f.Name == pair.Item1).Constant) == pair.Item2),
    "tactical voice asset IDs match the installed game's actual phrase enum");
Check(Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "Observe"), "SquadObserver::Threat")
    && !Calls(Method(plugin, "ImmersiveVoice.SquadObserver", "Hit"), "SquadMemory::Threat")
    && !Calls(Method(plugin, "ImmersiveVoice.SquadObserver", "Threat"), "ViewProbe::"),
    "tactical anchors reuse local visual observations without deriving positions from remote damage or adding a sight probe");
Check(Type(fika, "Fika.Core.Main.Components.ReviveInteractable").BaseType.FullName == "EFT.Interactive.InteractableObject"
    && !Calls(Method(fika, "Fika.Core.Main.Players.ObservedPlayer", "ToggleDowned"), "CreateCorpse"),
    "installed Fika's revivable ragdoll is not a real Corpse and downing does not create one");
Check(Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "UpdateVision"), "SquadObserver::Observe")
    && !Calls(Method(plugin, "ImmersiveVoice.SquadObserver", "Observe"), "ViewProbe::"),
    "teammate corpse confirmation reuses existing visible contacts without a corpse scan or new raycast");
Check(!Walk(new[] { Type(plugin, "ImmersiveVoice.VoiceJournal") }).SelectMany(t => t.Methods).Where(m => m.HasBody)
    .SelectMany(m => m.Body.Instructions).Any(i => i.Operand is MemberReference member && member.DeclaringType != null
        && (member.DeclaringType.FullName.StartsWith("EFT.") || member.DeclaringType.FullName.StartsWith("UnityEngine."))),
    "the diagnostic worker and buffer never access EFT or Unity objects");
Check(!Calls(Method(plugin, "ImmersiveVoice.VoiceJournal", "Record"), "System.IO.")
    && Calls(Method(plugin, "ImmersiveVoice.VoiceJournal", "WriteLoop"), "System.IO.TextWriter::WriteLine"),
    "runtime diagnostic producers enqueue strings while only the worker writes to disk");
Check(Calls(Method(fika, "Fika.Core.Main.ClientClasses.HandsControllers.FikaClientFirearmController", "InitiateShot"), "EFT.Player/FirearmController::InitiateShot"),
    "Fika local firing retains the native firearm shot event");
Check(Calls(Method(game, "EFT.BaseSpeaker", "PlayDirect"), "System.Action`4")
    && Type(game, "EFT.BaseSpeaker").Events.Any(e => e.Name == "OnPhraseTold"),
    "received native Fika voice clips retain the speaker event for nearby replies");
Check(Calls(Method(plugin, "ImmersiveVoice.VoiceSession", "SayNative"), "EFT.Player::Say")
    && !Walk(plugin.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).Any(m => Calls(m, "EFT.BaseSpeaker::PlayDirect")),
    "all automatic lines stay on Player.Say and Fika's original network path");
Check(!Walk(plugin.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.HasBody).Any(m => Calls(m, "TagBank::Match")),
    "clip preflight never consumes the native random clip rotation");
var selection = Method(plugin, "ImmersiveVoice.VoiceDirector", "Choose").Body.Instructions;
Check(Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "NativeHealthReason"), "HealthSpeechMemory::BlockReason")
    && Method(plugin, "ImmersiveVoice.VoiceSession", "Tick").Body.Instructions.Count(i =>
        i.Operand is MethodReference r && r.FullName.Contains("VoiceDirector::NativeHealthReason")) == 2,
    "native health hints are checked before preflight and again before native playback");
Check(!Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "Offer"), "HealthSpeechMemory::Consider")
    && !Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "Choose"), "HealthSpeechMemory::Consider")
    && Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "Choose"), "HealthSpeechMemory::Missing")
    && Calls(Method(plugin, "ImmersiveVoice.VoiceSession", "Attempt"), "VoiceDirector::FailedHealth"),
    "queueing and selection never consume wound speech; missing audio and playback failure are separate results");
Check(Calls(Method(plugin, "ImmersiveVoice.VoiceSession", "OnSpoken"), "VoiceDirector::ConsiderNativeHealth")
    && Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "Suspend"), "HealthSpeechMemory::Pause"),
    "actual native or manual wound speech shares memory that survives lifecycle suspension");
Check(Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "Valid"), "VoiceDirector::RefreshHealthCue")
    && Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "Choose"), "VoiceDirector::RefreshHealthCue"),
    "queued wound severity and identity refresh before policy gating as well as final playback selection");
Check(Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "Valid"), "SemanticVoice::PendingRejectionReason")
    && Calls(Method(plugin, "ImmersiveVoice.VoiceDirector", "Choose"), "SemanticVoice::RejectionReason"),
    "the actual director separates pending report survival from strict final playback validation");
var reportWait = Walk(new[] { Type(plugin, "ImmersiveVoice.VoiceDirector") }).SelectMany(t => t.Methods)
    .Single(m => m.HasBody && Calls(m, "SemanticVoice::WaitingReason"));
Check(reportWait.Name.Contains("<Next>") && Calls(reportWait, "VoicePolicy::BlockReason")
    && Calls(reportWait, "VoiceDirector::Waiting") && !Calls(reportWait, "VoiceDirector::get_Context")
    && !Calls(reportWait, "ViewProbe::"),
    "the director wait uses the existing damage clock without extra health snapshots or sight probes");
Check(Calls(Method(plugin, "ImmersiveVoice.SquadObserver", "Tick"), "SquadAwareness::ObserveReport")
    && !Calls(Method(plugin, "ImmersiveVoice.SquadObserver", "ReportNearby"), "ViewProbe::"),
    "team report continuity reuses sampled perception without a new sight query");
int probeIndex = selection.ToList().FindIndex(i => i.Operand is MethodReference r && r.Name == "Invoke"
    && r.DeclaringType.Name == "Func`2");
Check(probeIndex >= 0 && selection.Count(i => i.Operand is MethodReference r && r.Name == "Invoke"
        && r.DeclaringType.Name == "Func`2") == 1
    && !selection.Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Newarr
        || i.Operand is Mono.Cecil.Cil.Instruction target && target.Offset < i.Offset),
    "phrase selection probes one exact bank without a fallback array or retry loop");
Check(!selection.Skip(probeIndex + 1).Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld
        && i.Operand is FieldReference field && field.DeclaringType.Name == "VoiceCue"
        && (field.Name == "Phrase" || field.Name == "Intent")),
    "bank absence cannot change the requested phrase or intent into an available substitute");
Check(Method(plugin, "ImmersiveVoice.VoiceSession", "HasBank").Body.Instructions
    .Any(i => i.Operand is FieldReference f && f.DeclaringType.Name == "TaggedClip" && f.Name == "Clip"),
    "voice preflight inspects the actual audio reference, not just the bank entry");
Check(Type(fika, "Fika.Core.Main.Utils.FikaBackendUtils").Properties.Any(p => p.Name == "IsHeadless" && p.GetMethod.IsStatic), "optional headless detection matches installed Fika");
Check(Type(fika, "Fika.Core.Main.Utils.FikaBackendUtils").Properties.Any(p => p.Name == "IsServer" && p.GetMethod.IsStatic),
    "cached host/client policy adapter matches installed Fika");
Check(!Walk(plugin.MainModule.Types).SelectMany(t => t.Methods).Where(m => m.HasBody)
    .SelectMany(m => m.Body.Instructions).Any(i => i.Operand is string s && s == "IsHeadlessGame"),
    "connecting to headless is never used to disable a human client's director");
Check(Type(fika, "Fika.Core.Main.Players.FikaPlayer").Properties.Any(p => p.Name == "Downed"), "downed guard matches installed Fika");
Check(Method(fika, "Fika.Core.Main.GameMode.CoopGame", "Extract").Parameters[0].ParameterType.FullName == "Fika.Core.Main.Players.FikaPlayer", "extraction guard matches installed Fika");
int callsites = 0;
foreach (var type in Walk(plugin.MainModule.Types))
foreach (var method in type.Methods.Where(m => m.HasBody))
foreach (var instruction in method.Body.Instructions)
{
    if (instruction.Operand is not MemberReference member || member.DeclaringType == null) continue;
    var scope = member.DeclaringType.GetElementType().Scope.Name;
    if (scope != "Assembly-CSharp" && scope != "BepInEx" && scope != "0Harmony" && !scope.StartsWith("UnityEngine")) continue;
    if (member is MethodReference mr && mr.Resolve() == null) throw new Exception("Unresolved method: " + mr);
    if (member is FieldReference fr && fr.Resolve() == null) throw new Exception("Unresolved field: " + fr);
    callsites++;
}
Check(callsites > 100, $"{callsites} compiled game/BepInEx/Harmony/Unity member callsites resolve against installed DLLs");
Console.WriteLine($"SUCCESS: {assertions} checks plus the complete lifecycle matrix. Real raid/network playback still requires an in-game smoke test.");
#endif
