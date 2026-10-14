using System;
using BepInEx.Logging;
using BepInEx.Bootstrap;
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
        private struct PhraseScope { public Player Player; public TaggedClip Clip; public EPhraseTrigger Phrase; }
        [ThreadStatic] private static PhraseScope _scope;
        public static EPhraseTrigger Phrase(Player player, TaggedClip clip)
            => ReferenceEquals(_scope.Player, player) && ReferenceEquals(_scope.Clip, clip) ? _scope.Phrase : EPhraseTrigger.None;
        public static void Install(Harmony harmony, ManualLogSource log)
        {
            _log = log; _faulted = false;
            Scope(harmony, typeof(Player));
            if (Chainloader.PluginInfos.TryGetValue("com.fika.core", out var fika) && fika.Instance != null)
            {
                var assembly = fika.Instance.GetType().Assembly;
                Scope(harmony, assembly.GetType("Fika.Core.Main.Players.FikaPlayer"));
                Scope(harmony, assembly.GetType("Fika.Core.Main.Players.ObservedPlayer"));
            }
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Player), "PlayPhraseClip", new[] { typeof(TaggedClip) }),
                postfix: new HarmonyMethod(typeof(SpeechObservation), nameof(AfterSpeech)));
        }
        private static void Scope(Harmony harmony, Type type)
        {
            if (type == null) return;
            var method = AccessTools.DeclaredMethod(type, "OnPhraseTold",
                new[] { typeof(EPhraseTrigger), typeof(TaggedClip), typeof(TagBank), typeof(BaseSpeaker) });
            if (method != null) harmony.Patch(method, prefix: new HarmonyMethod(typeof(SpeechObservation), nameof(BeforePhrase)),
                finalizer: new HarmonyMethod(typeof(SpeechObservation), nameof(AfterPhrase)));
        }
        private static void BeforePhrase(Player __instance, EPhraseTrigger __0, TaggedClip __1, out PhraseScope __state)
        {
            __state = _scope;
            _scope = new PhraseScope { Player = __instance, Clip = __1, Phrase = __0 };
        }
        private static void AfterPhrase(PhraseScope __state) { _scope = __state; }
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
