using System;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace ImmersiveVoice
{
    // A synchronous, exact local attempt scope. No replacement clips, direct audio,
    // remote speakers, or persistent edits to native speaker state.
    internal static class NativePlayback
    {
        private static BaseSpeaker _speaker;
        private static EPhraseTrigger _phrase;
        private static TaggedClip _breath;
        private static bool _event;
        public static bool FemaleCompatibility { get; private set; }
        public static bool FemaleGateYielded { get; private set; }
        public static bool BreathYielded { get; private set; }
        public static bool Reached { get; private set; }
        public static string Result { get; private set; }
        public static ETagStatus Tags { get; private set; }

        public static void Install(Harmony harmony, ManualLogSource log)
        {
            FemaleCompatibility = false;
            var play = AccessTools.DeclaredMethod(typeof(BaseSpeaker), "Play");
            harmony.Patch(play, prefix: new HarmonyMethod(typeof(NativePlayback), nameof(BeforePlay)),
                postfix: new HarmonyMethod(typeof(NativePlayback), nameof(AfterPlay)));
            if (!Chainloader.PluginInfos.TryGetValue("com.20fpsguy.femalepmcvoice", out var female)) return;
            var type = female.Instance?.GetType().Assembly.GetType("FemalePMCSoundPatch.Plugin");
            var gate = type == null ? null : AccessTools.DeclaredMethod(type, "LocalSuppressTacticalCallouts",
                new[] { typeof(EPhraseTrigger) });
            if (gate == null || !gate.IsStatic || gate.ReturnType != typeof(bool))
            { log.LogWarning("Female voice compatibility unavailable: expected pain gate not found."); return; }
            try
            {
                harmony.Patch(gate, postfix: new HarmonyMethod(typeof(NativePlayback), nameof(AfterFemaleGate)));
                FemaleCompatibility = true;
            }
            catch (Exception ex) { log.LogWarning("Female voice compatibility unavailable: " + ex.GetType().Name); }
        }

        public static void Begin(BaseSpeaker speaker, EPhraseTrigger phrase, VoiceCue cue, TaggedClip breath)
        {
            _speaker = speaker; _phrase = phrase; _event = PlaybackRules.EventVoice(cue); _breath = breath;
            FemaleGateYielded = BreathYielded = Reached = false; Result = "NativePlayNotObserved"; Tags = 0;
        }
        public static void End() { _speaker = null; _breath = null; _event = false; }
        public static Exception TrySpeak(Action speak)
        {
            // Only the synchronous automatic call is isolated. Native/manual
            // calls and unrelated director errors retain their normal handling.
            try { speak(); return null; }
            catch (NullReferenceException ex) { return ex; }
            catch (MissingReferenceException ex) { return ex; }
        }
        private static bool Own(BaseSpeaker speaker, EPhraseTrigger phrase) => _speaker != null
            && ReferenceEquals(speaker, _speaker) && phrase == _phrase;
        private static void AfterFemaleGate(EPhraseTrigger __0, ref bool __result)
        {
            // This method is called by the female plugin only for its local speaker.
            // The scope is entered exclusively by a validated local VoiceSession.
            if (__result && _speaker != null && _event && __0 == _phrase)
            { __result = false; FemaleGateYielded = true; }
        }
        private static void BeforePlay(BaseSpeaker __instance, EPhraseTrigger __0, ETagStatus __1, ref int? importance)
        {
            if (!Own(__instance, __0)) return;
            Reached = true; Tags = __1;
            if (!_event || _breath == null || !ReferenceEquals(_breath, __instance.Clip) || !__instance.Busy
                || (__instance.QueuedEvent != EPhraseTrigger.None && __instance.QueuedEvent != EPhraseTrigger.PhraseNone
                    && __instance.QueuedEvent != EPhraseTrigger.OnBreath)) return;
            if (__instance.Importance == int.MaxValue) return;
            int bankPriority = __instance.PhrasesBanks.TryGetValue(__0, out var bank) && bank != null ? bank.Importance : 0;
            importance = Math.Max(importance ?? bankPriority, __instance.Importance + 1);
            BreathYielded = true;
        }
        private static void AfterPlay(BaseSpeaker __instance, EPhraseTrigger __0, TagBank __result)
        {
            if (!Own(__instance, __0)) return;
            Result = __result == null ? "NativeNoBankReturned" : "NativeBankReturned";
        }
    }
}
