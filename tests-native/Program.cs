using System;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using EFT;
using HarmonyLib;
using ImmersiveVoice;

int count = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    count++; Console.WriteLine("PASS: " + name);
}
const string femaleId = "com.20fpsguy.femalepmcvoice";
var log = new ManualLogSource();
var harmony = new Harmony("test.immersivevoice");
NativePlayback.Install(harmony, log);
Check(!NativePlayback.FemaleCompatibility, "optional plugin absence still permits native hook installation");
harmony.UnpatchSelf();
Chainloader.PluginInfos[femaleId] = new PluginInfo { Instance = new object() };
NativePlayback.Install(harmony, log);
Check(!NativePlayback.FemaleCompatibility && log.Warnings == 1, "incompatible optional plugin does not disable native hooks");
harmony.UnpatchSelf();
Chainloader.PluginInfos[femaleId] = new PluginInfo();
NativePlayback.Install(harmony, log);
Check(!NativePlayback.FemaleCompatibility && log.Warnings == 2, "an optional plugin that failed to instantiate cannot disable automatic voice");
harmony.UnpatchSelf();
Chainloader.PluginInfos[femaleId] = new PluginInfo { Instance = new FemalePMCSoundPatch.Plugin() };
foreach (bool femaleFirst in new[] { true, false })
{
    var female = new Harmony("test.femalevoice");
    var play = AccessTools.Method(typeof(BaseSpeaker), "Play");
    void PatchFemale() => female.Patch(play, prefix: new HarmonyMethod(typeof(FemalePMCSoundPatch.Plugin), "Prefix"));
    if (femaleFirst) PatchFemale();
    NativePlayback.Install(harmony, log);
    if (!femaleFirst) PatchFemale();
    try
    {
        Check(NativePlayback.FemaleCompatibility, "compatible female gate patched with either prefix order");
        var speaker = new BaseSpeaker();
        FemalePMCSoundPatch.Plugin.LocalSpeaker = speaker;
        var bank = new TagBank { Importance = 1 };
        speaker.PhrasesBanks[EPhraseTrigger.StartHeal] = bank;
        speaker.PhrasesBanks[EPhraseTrigger.OnMutter] = bank;
        var cue = new VoiceCue { Intent = VoiceIntent.Healing };
        Check(speaker.Play(EPhraseTrigger.StartHeal, 0) == null, "unscoped native/manual call keeps female gate behavior");
        NativePlayback.Begin(speaker, EPhraseTrigger.StartHeal, cue, null);
        Check(speaker.Play(EPhraseTrigger.StartHeal, ETagStatus.Combat) == bank
            && NativePlayback.FemaleGateYielded && NativePlayback.Reached
            && NativePlayback.Tags == ETagStatus.Combat && NativePlayback.Result == "NativeBankReturned",
            "exact scoped healing uses the original Play result and observed tags");
        NativePlayback.End();
        Check(speaker.Play(EPhraseTrigger.StartHeal, 0) == null, "compatibility ends immediately after the attempt");
        NativePlayback.Begin(speaker, EPhraseTrigger.StartHeal, cue, null);
        Check(speaker.Play(EPhraseTrigger.OnMutter, 0) == null && !NativePlayback.FemaleGateYielded,
            "a different phrase cannot borrow the event scope");
        NativePlayback.End();
        NativePlayback.Begin(speaker, EPhraseTrigger.OnMutter, new VoiceCue { Intent = VoiceIntent.CalmMutter }, null);
        Check(speaker.Play(EPhraseTrigger.OnMutter, 0) == null && !NativePlayback.FemaleGateYielded,
            "ordinary chatter keeps female pain suppression");
        NativePlayback.End();
        NativePlayback.Begin(speaker, EPhraseTrigger.OnFirstContact, new VoiceCue { Intent = VoiceIntent.Contact }, null);
        Check(speaker.Play(EPhraseTrigger.OnFirstContact, 0) == null && NativePlayback.Result == "NativeNoBankReturned",
            "compatibility cannot manufacture a missing bank");
        NativePlayback.End();

        var breath = new TaggedClip(); speaker.Clip = breath; speaker.Busy = true; speaker.Importance = 1;
        NativePlayback.Begin(speaker, EPhraseTrigger.StartHeal, cue, breath);
        Check(speaker.Play(EPhraseTrigger.StartHeal, 0) == bank && NativePlayback.BreathYielded,
            "real Harmony ref nullable priority injection permits native replacement of the captured breath");
        NativePlayback.End();
        speaker.QueuedEvent = EPhraseTrigger.OnMutter;
        NativePlayback.Begin(speaker, EPhraseTrigger.StartHeal, cue, breath);
        Check(speaker.Play(EPhraseTrigger.StartHeal, 0) == null && !NativePlayback.BreathYielded,
            "an unrelated queued phrase cannot be overwritten by the breath exception");
        NativePlayback.End(); speaker.QueuedEvent = EPhraseTrigger.None;
        speaker.Clip = new TaggedClip();
        NativePlayback.Begin(speaker, EPhraseTrigger.StartHeal, cue, breath);
        Check(speaker.Play(EPhraseTrigger.StartHeal, 0) == null && !NativePlayback.BreathYielded,
            "a changed current clip invalidates captured breath permission");
        NativePlayback.End(); speaker.Clip = breath;
        var remote = new BaseSpeaker { Clip = breath, Busy = true, Importance = 1 };
        remote.PhrasesBanks[EPhraseTrigger.StartHeal] = bank;
        NativePlayback.Begin(speaker, EPhraseTrigger.StartHeal, cue, breath);
        Check(remote.Play(EPhraseTrigger.StartHeal, 0) == null && !NativePlayback.BreathYielded && !NativePlayback.Reached,
            "another speaker cannot inherit local priority or diagnostics");
        NativePlayback.End(); speaker.Importance = int.MaxValue;
        NativePlayback.Begin(speaker, EPhraseTrigger.StartHeal, cue, breath);
        Check(speaker.Play(EPhraseTrigger.StartHeal, 0) == null && !NativePlayback.BreathYielded,
            "maximum native priority cannot overflow into a replacement");
        NativePlayback.End();
    }
    finally { NativePlayback.End(); harmony.UnpatchSelf(); female.UnpatchSelf(); }
}
Console.WriteLine("SUCCESS: " + count + " real-Harmony checks with fixtures; no Unity audio or network test.");
