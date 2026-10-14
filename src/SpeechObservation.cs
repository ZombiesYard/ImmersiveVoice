using System;
using BepInEx.Logging;
using EFT;
using HarmonyLib;

namespace ImmersiveVoice
{
    // Both native speakers and Fika's observed speakers reach this non-virtual
    // audio endpoint. Never change its arguments, result or network behavior.
    internal static class SpeechObservation
    {
        public static event Action<Player, TaggedClip, BetterSource> Played;
        private static ManualLogSource _log;
        private static bool _faulted;
        public static void Install(Harmony harmony, ManualLogSource log)
        {
            _log = log; _faulted = false;
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Player), "PlayPhraseClip", new[] { typeof(TaggedClip) }),
                postfix: new HarmonyMethod(typeof(SpeechObservation), nameof(AfterSpeech)));
        }
        private static void AfterSpeech(Player __instance, TaggedClip __0, BetterSource ____speechSource, bool __runOriginal)
        {
            if (!__runOriginal || _faulted) return;
            try { Played?.Invoke(__instance, __0, ____speechSource); }
            catch (Exception ex)
            {
                _faulted = true;
                _log.LogWarning("NPC speech observation disabled after callback error: " + ex.GetType().Name);
            }
        }
    }
}
