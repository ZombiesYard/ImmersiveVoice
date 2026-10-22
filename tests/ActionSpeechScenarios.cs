using System.Diagnostics;
using ImmersiveVoice;

internal static class ActionSpeechScenarios
{
    private static TreatmentSnapshot Health(float hp = 200f, InjuryLevel injury = InjuryLevel.BadlyInjured,
        int bleeds = 1, int heavy = 0, int fractures = 0)
        => new() { Health = hp, Injury = injury, Bleeds = bleeds, HeavyBleeds = heavy, Fractures = fractures };
    private static TreatmentReadyState Treatment(TreatmentSnapshot health, int damage = 1,
        bool active = true, bool canAct = false, bool teammate = true)
        => new() { Health = health, DamageVersion = damage, OperationActive = active,
            CanAct = canAct, NearbyHuman = teammate };
    private static GeneralHelpState Help(bool unavailable = true, bool teammate = true)
        => new() { WeaponPresent = true, WeaponReady = !unavailable, Operating = unavailable,
            AmmoKnown = true, Ammo = 20, NearbyHuman = teammate };

    public static void Run(Action<bool, string> check)
    {
        var search = new SearchSpeechMemory();
        check(!search.Begin(1, SearchSpeechTarget.None, 0),
            "inventory panels, pickups and untyped item proximity cannot open OnLoot");
        check(search.Begin(10, SearchSpeechTarget.Corpse, 0) && search.Take(.34f) == 0,
            "a real local corpse search opens one short opportunity without firing on the same callback");
        int version = search.Take(.35f);
        check(version > 0 && search.Valid(version, .5f) && search.Take(.6f) == 0,
            "one active search produces one OnLoot decision even if its exact bank is absent");
        search.End(10, true);
        check(!search.Valid(version, .7f), "search completion invalidates queued OnLoot speech");

        search = new SearchSpeechMemory();
        check(search.Begin(20, SearchSpeechTarget.Container, 1) && !search.Begin(20, SearchSpeechTarget.Container, 2),
            "duplicate SearchOperations cannot renew the reaction window");
        search.Danger();
        check(search.Take(2.5f) == 0, "actual danger cancels an active search line");
        check(search.Begin(21, SearchSpeechTarget.Container, 3), "a genuinely new search gets its own episode");
        search.End(21, false);
        check(search.Take(3.5f) == 0, "cancelled searches stay silent");
        check(search.Begin(22, SearchSpeechTarget.Container, 4) && search.Take(8) == 0,
            "an unhandled search opportunity expires instead of becoming late speech");
        search.Pause();

        var help = new GeneralHelpMemory();
        check(!help.Danger(0, 0), "a missing hostile-damage sequence cannot authorize NeedHelp");
        check(help.Danger(0, 1) && help.Take(.1f, Help()) > 0,
            "recent real danger plus a temporarily unusable weapon and perceived human teammate supports NeedHelp");
        check(help.Take(.2f, Help()) == 0, "one danger cluster receives at most one broad help decision");
        help = new GeneralHelpMemory(); help.Danger(0, 1);
        check(help.Take(.1f, Help(teammate: false)) == 0 && help.Take(1, Help()) > 0,
            "NeedHelp waits for an actually perceived human recipient inside the short danger window");
        foreach (string exact in new[] { "cover", "medkit", "ammo", "empty" })
        {
            help = new GeneralHelpMemory(); help.Danger(0, 1); var state = Help();
            state.PreciseCover = exact == "cover"; state.PreciseMedkit = exact == "medkit";
            state.PreciseAmmo = exact == "ammo"; if (exact == "empty") state.Ammo = 0;
            check(help.Take(.1f, state) == 0 && help.Take(.2f, Help()) == 0,
                exact + " consumes the episode as a more precise request instead of falling back to NeedHelp");
        }
        help = new GeneralHelpMemory(); help.Danger(0, 1);
        check(help.Take(.1f, Help(unavailable: false)) == 0,
            "danger and a teammate do not imply help while the weapon is usable");
        check(help.Take(5.01f, Help()) == 0, "old danger cannot authorize a delayed broad request");
        help = new GeneralHelpMemory(); help.Danger(0, 1); version = help.Take(.1f, Help());
        check(!help.Valid(version, .2f, Help(unavailable: false)) && !help.Valid(version, .2f, Help(teammate: false)),
            "weapon recovery or recipient loss cancels queued NeedHelp speech");

        var before = Health(); var better = Health(240, InjuryLevel.Injured, 0);
        var ready = new TreatmentReadyMemory();
        check(ready.Begin(100, 0, Treatment(before)), "a real local medicine operation opens treatment observation");
        ready.Observe(2, Treatment(better));
        check(ready.End(100, 3, true, Treatment(better, active: false, canAct: false)),
            "successful operation completion is recorded separately from health improvement");
        check(ready.Take(3.39f, Treatment(better, active: false, canAct: true)) == 0,
            "treatment Ready waits for restored control and a stable completion edge");
        version = ready.Take(3.4f, Treatment(better, active: false, canAct: true));
        check(version > 0 && ready.Valid(version, 3.5f, Treatment(better, active: false, canAct: true)),
            "observed improvement, completed treatment, restored action and a nearby teammate yield one Ready");
        check(ready.Take(3.6f, Treatment(better, active: false, canAct: true)) == 0,
            "the same treatment segment cannot retry a missing Ready bank");

        ready = new TreatmentReadyMemory(); ready.Begin(101, 0, Treatment(before));
        ready.Observe(1, Treatment(better)); ready.End(101, 2, false, Treatment(better, active: false));
        check(ready.Take(3, Treatment(better, active: false, canAct: true)) == 0,
            "cancelled treatment stays silent even if some state improved before cancellation");
        ready = new TreatmentReadyMemory(); ready.Begin(102, 0, Treatment(before));
        ready.End(102, 1, true, Treatment(before, active: false, canAct: true));
        check(ready.Take(3.01f, Treatment(before, active: false, canAct: true)) == 0,
            "completed medicine with no observed improvement does not claim Ready");
        ready = new TreatmentReadyMemory(); ready.Begin(103, 0, Treatment(before));
        ready.Observe(1, Treatment(better, damage: 2));
        check(ready.Take(2, Treatment(better, damage: 2, active: false, canAct: true)) == 0,
            "new damage cancels the treatment closing");
        ready = new TreatmentReadyMemory(); ready.Begin(110, 0, Treatment(before)); ready.Observe(1, Treatment(better));
        check(ready.Begin(111, 1.1f, Treatment(better)),
            "an unconfirmed replacement starts a new segment instead of inheriting a cancelled operation");
        ready.End(111, 2, true, Treatment(better, active: false, canAct: true));
        check(ready.Take(4.01f, Treatment(better, active: false, canAct: true)) == 0,
            "improvement from a replaced active operation cannot manufacture Ready for its successor");
        ready = new TreatmentReadyMemory(); ready.Begin(104, 0, Treatment(before));
        var worse = Health(240, InjuryLevel.Injured, 0, heavy: 1);
        ready.Observe(1, Treatment(worse));
        check(ready.Take(2, Treatment(worse, active: false, canAct: true)) == 0,
            "a new wound cancels the old improvement narrative");
        ready = new TreatmentReadyMemory(); ready.Begin(105, 0, Treatment(before));
        ready.Observe(1, Treatment(better)); ready.End(105, 2, true, Treatment(better, active: false, canAct: true, teammate: false));
        check(ready.Take(2.5f, Treatment(better, active: false, canAct: true, teammate: false)) == 0,
            "treatment completion without a remaining human companion stays silent");

        ready = new TreatmentReadyMemory(); ready.Begin(106, 0, Treatment(before));
        ready.Observe(1, Treatment(better)); ready.End(106, 2, true, Treatment(better, active: false));
        check(ready.Begin(107, 2.5f, Treatment(better)),
            "a prompt second medicine continues the same treatment segment");
        ready.Observe(3, Treatment(Health(270, InjuryLevel.Healthy, 0)));
        var healed = Health(270, InjuryLevel.Healthy, 0);
        ready.End(107, 4, true, Treatment(healed, active: false, canAct: true));
        version = ready.Take(4.4f, Treatment(healed, active: false, canAct: true));
        check(version > 0, "continued treatment speaks only after the final operation restores action");
        ready = new TreatmentReadyMemory(); ready.Begin(108, 0, Treatment(before)); ready.Observe(1, Treatment(better));
        ready.End(108, 2, true, Treatment(better, active: false, canAct: true));
        version = ready.Take(2.4f, Treatment(better, active: false, canAct: true));
        check(ready.Begin(109, 2.5f, Treatment(better))
            && !ready.Valid(version, 2.5f, Treatment(better)) ,
            "continued medicine invalidates a queued Ready and cannot overlap treatment");
        ready.Observe(3, Treatment(healed)); ready.End(109, 4, true, Treatment(healed, active: false, canAct: true));
        check(ready.Take(4.4f, Treatment(healed, active: false, canAct: true)) == 0,
            "a selected or missing Ready does not reroll later in the same continued segment");

        // Pure event-state cost only; no inventories, scene objects, raycasts,
        // network messages or LINQ enumerators participate in this benchmark.
        search = new SearchSpeechMemory(); help = new GeneralHelpMemory(); ready = new TreatmentReadyMemory();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        long watchBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        for (int i = 1; i <= 100000; i++)
        {
            float now = i * 3f;
            search.Begin(i, (i & 1) == 0 ? SearchSpeechTarget.Container : SearchSpeechTarget.Corpse, now);
            search.Take(now + .35f); search.End(i, true);
            help.Clear(); help.Danger(now, i); help.Take(now + .1f, Help());
            ready.Clear(); ready.Begin(i, now, Treatment(before)); ready.Observe(now + 1, Treatment(better));
            ready.End(i, now + 2, true, Treatment(better, active: false, canAct: true));
            ready.Take(now + 2.4f, Treatment(better, active: false, canAct: true));
        }
        watch.Stop();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated - watchBytes;
        check(bytes == 0, "100000 search, help and treatment action cycles allocate zero managed bytes");
        Console.WriteLine($"ACTION SPEECH BENCHMARK: 100000 event cycles in {watch.Elapsed.TotalMilliseconds:F2} ms, managed allocation {bytes} bytes; pure model only.");
    }
}
