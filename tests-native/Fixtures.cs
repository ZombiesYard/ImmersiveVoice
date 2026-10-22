using System.Collections.Generic;
using System.Runtime.CompilerServices;

// Signature-compatible fixtures only. No Unity, audio, game loop or Fika network
// is executed here. The real installed method shapes are checked by tests/Program.cs.
public enum EPhraseTrigger { None, PhraseNone, OnBreath, StartHeal, OnMutter, OnFirstContact }
public enum ETagStatus { None, Combat }
public class TaggedClip { }
public class TagBank { public int Importance; }
public class BetterSource { }
namespace EFT
{
    public class TestHealth { public bool IsAlive = true; }
    public class Player
    {
        protected BetterSource _speechSource = new BetterSource();
        public int PlayCount;
        public bool IsAI;
        public object GameWorld;
        public string GroupId = "group";
        public TestHealth HealthController = new TestHealth();
        public bool ThrowOnAudio;
        public bool AudioEnabled = true;
        public float LastStart;
        public System.Action DuringAudio;
        public BetterSource Source => _speechSource;
        // Intentionally inlineable, matching the installed native forwarding method.
        public void PlayPhraseClip(TaggedClip clip) { PlaySpeechFromTime(clip, 0f); }
        public void PlaySpeechFromTime(TaggedClip clip, float time)
        {
            if (ThrowOnAudio) throw new System.NullReferenceException("private-message-token");
            if (clip == null) throw new System.ArgumentNullException(nameof(clip));
            if (time < 0) throw new System.ArgumentOutOfRangeException(nameof(time));
            if (!AudioEnabled) return;
            LastStart = time;
            PlayCount++;
            DuringAudio?.Invoke();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual void OnPhraseTold(EPhraseTrigger phrase, TaggedClip clip, TagBank bank, BaseSpeaker speaker)
        { PlayPhraseClip(clip); }
    }
    public class BaseSpeaker
    {
        public Dictionary<EPhraseTrigger, TagBank> PhrasesBanks = new Dictionary<EPhraseTrigger, TagBank>();
        public TaggedClip Clip { get; set; }
        public bool Busy { get; set; }
        public int Importance { get; set; }
        public EPhraseTrigger QueuedEvent { get; set; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public TagBank Play(EPhraseTrigger trigger, ETagStatus tags, bool demand = false, int? importance = null)
        {
            if (!PhrasesBanks.TryGetValue(trigger, out var bank)) return null;
            return Busy && (importance ?? bank.Importance) <= Importance ? null : bank;
        }
    }
}
public static class SuppressedAudioFixture
{
    public static bool Prefix() => false;
}
namespace BepInEx.Bootstrap
{
    public class PluginInfo { public object Instance; }
    public static class Chainloader
    {
        public static Dictionary<string, PluginInfo> PluginInfos = new Dictionary<string, PluginInfo>();
    }
}
namespace BepInEx.Logging
{
    public class ManualLogSource
    {
        public int Warnings;
        public void LogWarning(object message) { Warnings++; System.Console.WriteLine(message); }
        public void LogInfo(object message) { }
    }
}
namespace FemalePMCSoundPatch
{
    public class Plugin
    {
        public static EFT.BaseSpeaker LocalSpeaker;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool LocalSuppressTacticalCallouts(EPhraseTrigger trigger) => trigger != EPhraseTrigger.OnBreath;
        public static bool Prefix(EFT.BaseSpeaker __instance, EPhraseTrigger __0)
            => __instance != LocalSpeaker || !LocalSuppressTacticalCallouts(__0);
    }
}

namespace Fika.Core.Main.Players
{
    public class FikaPlayer : EFT.Player
    {
        public bool IsObservedAI;
        public int NetId;
        public bool Downed { get; set; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void OnPhraseTold(EPhraseTrigger phrase, TaggedClip clip, TagBank bank, EFT.BaseSpeaker speaker)
        { base.OnPhraseTold(phrase, clip, bank, speaker); }
    }
    public class ObservedPlayer : FikaPlayer
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void OnPhraseTold(EPhraseTrigger phrase, TaggedClip clip, TagBank bank, EFT.BaseSpeaker speaker)
        { if (!Fika.Core.Main.Utils.FikaBackendUtils.IsHeadless) PlayPhraseClip(clip); }
    }
}

namespace UnityEngine
{
    public static class Time { public static float time; }
    public static class Application { public static bool isBatchMode; }
    public class MissingReferenceException : System.Exception { }
}
namespace Fika.Core.Main.Utils
{
    public static class FikaBackendUtils
    {
        public static bool IsHeadless { get; set; }
        public static bool IsServer { get; set; }
    }
}
namespace Fika.Core.Main.GameMode
{
    public class CoopGame { public void Extract(Fika.Core.Main.Players.FikaPlayer player) { } }
}
