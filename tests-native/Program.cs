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
        NativePlayback.Begin(speaker, (EPhraseTrigger)63, new VoiceCue { Intent = VoiceIntent.TeamReady }, null);
        Check(speaker.Play((EPhraseTrigger)63, 0) == null && NativePlayback.Result == "NativeNoBankReturned",
            "Ready closing with no exact bank remains silent even though other phrases exist");

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

var soloBridge = new FikaBridge();
Check(!soloBridge.IsAI(null) && !soloBridge.IsAI(new Player()) && soloBridge.IsAI(new Player { IsAI = true }),
    "SPT without Fika distinguishes native AI, humans and missing players");
SpeechObservation.Install(harmony, log);
var playerFixture = new Player();
var speechClip = new TaggedClip();
int heard = 0;
Action<Player, TaggedClip, BetterSource> listener = (p, c, source) =>
{
    Check(p == playerFixture && c == speechClip && source == playerFixture.Source && p.PlayCount == 1,
        "audio postfix receives the real protected source field after the original method");
    heard++;
};
SpeechObservation.Played += listener;
playerFixture.PlayPhraseClip(speechClip);
var suppressor = new Harmony("test.suppressed.audio");
suppressor.Patch(AccessTools.Method(typeof(Player), "PlaySpeechFromTime"),
    prefix: new HarmonyMethod(typeof(SuppressedAudioFixture), "Prefix"));
playerFixture.PlayPhraseClip(speechClip);
Check(heard == 1 && playerFixture.PlayCount == 1, "a skipped native audio call is not observed as heard speech");
suppressor.UnpatchSelf();
SpeechObservation.Played -= listener;
playerFixture.PlayPhraseClip(speechClip);
Check(heard == 1, "disposing the listening session removes audio callbacks");
Action<Player, TaggedClip, BetterSource> badListener = (p, c, source) => throw new InvalidOperationException();
SpeechObservation.Played += badListener;
int warnings = log.Warnings;
playerFixture.PlayPhraseClip(speechClip);
playerFixture.PlayPhraseClip(speechClip);
Check(log.Warnings == warnings + 1 && playerFixture.PlayCount == 4,
    "observer failure is isolated and logged once without blocking native playback");
SpeechObservation.Played -= badListener;
harmony.UnpatchSelf();

Chainloader.PluginInfos["com.fika.core"] = new PluginInfo { Instance = new Fika.Core.Main.Players.FikaPlayer() };
var bridge = new FikaBridge();
var remoteBot = new Fika.Core.Main.Players.ObservedPlayer { IsAI = false, IsObservedAI = true };
var remoteHuman = new Fika.Core.Main.Players.ObservedPlayer { IsAI = false, IsObservedAI = false };
Check(!remoteBot.IsAI && bridge.IsAI(remoteBot) && !bridge.IsAI(remoteHuman),
    "Fika client identifies a replicated bot without a local BotOwner and preserves remote human identity");
Check(!bridge.IsAI(new Player()) && bridge.IsAI(new Player { IsAI = true }) && !bridge.IsAI(null),
    "optional Fika reader also accepts native SPT entities without invalid casts");
Check(VisualRules.RecognizableEnemy(false, bridge.IsAI(remoteBot), true, false, true)
    && !VisualRules.RecognizableEnemy(false, bridge.IsAI(remoteHuman), true, true, false)
    && !VisualRules.RecognizableEnemy(false, bridge.IsAI(remoteBot), false, false, true),
    "PMC recognizes a visible replicated Scav; humans and neutral Scav encounters keep their boundaries");
remoteHuman.Downed = true;
Check(bridge.Downed(remoteHuman) && !bridge.Downed(new Player()) && bridge.Mode(remoteBot) == VoiceMode.FikaClient,
    "AI adaptation preserves optional Fika downed and client checks");
SpeechObservation.Install(harmony, log);
EPhraseTrigger observedPhrase = EPhraseTrigger.None;
Action<Player, TaggedClip, BetterSource> exactListener = (p, c, source) => observedPhrase = SpeechObservation.Phrase(p, c);
SpeechObservation.Played += exactListener;
foreach (Player source in new Player[] { new Player(), new Fika.Core.Main.Players.FikaPlayer(), new Fika.Core.Main.Players.ObservedPlayer() })
{
    source.OnPhraseTold(EPhraseTrigger.OnMutter, speechClip, new TagBank(), new BaseSpeaker());
    Check(observedPhrase == EPhraseTrigger.OnMutter && source.PlayCount == 1,
        "native and Fika paths retain the exact transmitted trigger through audio playback");
    Check(SpeechObservation.Phrase(source, speechClip) == EPhraseTrigger.None,
        "nested native/Fika phrase scopes restore after each call");
    source.PlayPhraseClip(speechClip);
    Check(observedPhrase == EPhraseTrigger.None, "direct third-party playback never inherits a stale phrase");
}
int observedBefore = heard;
Action<Player, TaggedClip, BetterSource> counter = (p, c, source) => heard++;
SpeechObservation.Played += counter;
remoteBot.OnPhraseTold(EPhraseTrigger.OnMutter, speechClip, new TagBank(), new BaseSpeaker());
Check(heard == observedBefore + 1 && observedPhrase == EPhraseTrigger.OnMutter,
    "replicated NPC reaches the real audio observer once through an inlineable forwarding method");
remoteBot.PlaySpeechFromTime(speechClip, 1f);
Check(heard == observedBefore + 1, "resuming an existing clip cannot invent a second NPC turn");
Fika.Core.Main.Utils.FikaBackendUtils.IsHeadless = true;
remoteBot.OnPhraseTold(EPhraseTrigger.OnMutter, speechClip, new TagBank(), new BaseSpeaker());
Check(heard == observedBefore + 1, "headless suppression never produces an NPC observation");
Fika.Core.Main.Utils.FikaBackendUtils.IsHeadless = false;
remoteBot.ThrowOnAudio = true;
var audioError = NativePlayback.TrySpeak(() => remoteBot.OnPhraseTold(EPhraseTrigger.OnMutter, speechClip, new TagBank(), new BaseSpeaker()));
Check(audioError is NullReferenceException && heard == observedBefore + 1
    && SpeechObservation.Phrase(remoteBot, speechClip) == EPhraseTrigger.None,
    "throwing native audio is not observed and the Fika phrase scope is restored");
string details = FaultDetails.Format(audioError);
Check(details.Contains("NullReferenceException") && details.Contains("PlaySpeechFromTime")
    && !details.Contains("private-message-token") && !details.Contains("Fixtures.cs"),
    "durable fault details retain call sites without exception messages or source paths");
remoteBot.ThrowOnAudio = false;
Check(NativePlayback.TrySpeak(() => remoteBot.OnPhraseTold(EPhraseTrigger.OnMutter, speechClip, new TagBank(), new BaseSpeaker())) == null
    && heard == observedBefore + 2, "a later native phrase can play after an isolated audio exception");
Check(NativePlayback.TrySpeak(() => { throw new UnityEngine.MissingReferenceException(); }) is UnityEngine.MissingReferenceException,
    "destroyed Unity objects are isolated at the same automatic call boundary");
bool unexpectedPropagated = false;
try { NativePlayback.TrySpeak(() => { throw new InvalidOperationException(); }); }
catch (InvalidOperationException) { unexpectedPropagated = true; }
Check(unexpectedPropagated, "unrelated failures are not silently treated as recoverable audio errors");
SpeechObservation.Played -= counter;
SpeechObservation.Played -= exactListener;
harmony.UnpatchSelf();
DialogueNetworkScenarios.Run(Check, new FikaBridge());
Console.WriteLine("SUCCESS: " + count + " Harmony and four-client metadata fixture checks; no Unity audio or live UDP test.");
