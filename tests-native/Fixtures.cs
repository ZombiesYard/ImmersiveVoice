using System.Collections.Generic;
using System.Runtime.CompilerServices;

// Signature-compatible fixtures only. No Unity, audio, game loop or Fika network
// is executed here. The real installed method shapes are checked by tests/Program.cs.
public enum EPhraseTrigger { None, PhraseNone, OnBreath, StartHeal, OnMutter, OnFirstContact }
public enum ETagStatus { None, Combat }
public class TaggedClip { }
public class TagBank { public int Importance; }
namespace EFT
{
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
        public void LogWarning(object message) { Warnings++; }
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
