using System;
using BepInEx;
using BepInEx.Logging;
using EFT;
using EFT.Ballistics;
using EFT.UI.Gestures;
using HarmonyLib;
using UnityEngine;

namespace ImmersiveVoice
{
    [BepInPlugin(Id, "Immersive Voice 沉浸式角色语音", Version)]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.fika.headless", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.20fpsguy.femalepmcvoice", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(ConfigurationMenu.PluginId, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "local.immersivevoice";
        public const string Version = "2.16.0";
        private static Plugin _instance;
        private Harmony _harmony;
        private Settings _settings;
        private FikaBridge _fika;
        internal static FikaDialogueNetwork Dialogue { get; private set; }
        private VoiceSession _session;
        private float _nextTick;
        private bool _failed;

        private void Awake()
        {
            _instance = this;
            _settings = new Settings(Config);
            try
            {
                _fika = new FikaBridge();
                if (_fika.Assembly != null)
                {
                    try { Dialogue = new FikaDialogueNetwork(_fika.Assembly, _fika, Logger); }
                    catch (Exception ex) { Logger.LogWarning("Squad dialogue transport unavailable: " + FaultDetails.Format(ex)); }
                }
                if (_fika.Headless)
                {
                    Logger.LogInfo("Headless client detected: dialogue arbitration only; automatic voice is inactive; no voice patches installed.");
                    enabled = false;
                    return;
                }
                _harmony = new Harmony(Id);
                NativePlayback.Install(_harmony, Logger);
                SpeechObservation.Install(_harmony, Logger);
                Patch(typeof(GesturesQuickPanel), "Show", nameof(OnPanelShown), false);
                Patch(typeof(GamePlayerOwner), "QuickMumbleHandler", nameof(OnManualInput), true);
                Patch(typeof(GamePlayerOwner), "MakeBindActionHandler", nameof(OnManualInput), true);
                if (_fika.Extract != null)
                {
                    Patch(typeof(Player), "ShotReactions", nameof(OnObservedHit), false);
                    _harmony.Patch(_fika.Extract, postfix: new HarmonyMethod(typeof(Plugin), nameof(OnExtracted)));
                }
                Logger.LogInfo("Immersive Voice " + Version + " ready. Evidence-bound combat reports and four-player tactics. Missing clips stay silent. Local player only. Pause toggles silence; F12 config.");
            }
            catch (Exception ex)
            {
                _harmony?.UnpatchSelf();
                _failed = true;
                Logger.LogError("Automatic voice disabled because initialization failed. " + ex);
            }
        }

        private void Patch(Type type, string method, string patch, bool prefix)
        {
            var original = AccessTools.DeclaredMethod(type, method);
            if (original == null) throw new MissingMethodException(type.FullName, method);
            var hook = new HarmonyMethod(typeof(Plugin), patch);
            _harmony.Patch(original, prefix: prefix ? hook : null, postfix: prefix ? null : hook);
        }

        private static void OnPanelShown(GesturesQuickPanel __instance, GamePlayerOwner owner)
        {
            Plugin plugin = _instance;
            if (plugin == null || plugin._failed) return;
            try
            {
                if (plugin._fika.Headless || owner == null || owner is HideoutPlayerOwner
                    || owner.Player == null || !owner.Player.IsYourPlayer || plugin._fika.IsAI(owner.Player)) return;
                if (plugin._session != null && plugin._session.Owner == owner
                    && plugin._session.Panel == __instance) return;
                plugin.StopSession();
                plugin._session = new VoiceSession(owner, __instance, plugin._settings, plugin._fika, plugin.Logger);
            }
            catch (Exception ex) { plugin.FailSession(ex); }
        }

        private static void OnManualInput(GamePlayerOwner __instance)
        {
            try
            {
                if (_instance?._session?.Owner == __instance) _instance._session.ManualInput();
            }
            catch (Exception ex) { _instance?.FailSession(ex); }
        }

        private static void OnExtracted(object __0)
        {
            Plugin plugin = _instance;
            if (plugin != null && plugin._session != null && __0 is Player player
                && plugin._session.Player == player) plugin.StopSession();
        }

        private static void OnObservedHit(Player __instance, DamageInfo __0)
        {
            Plugin plugin = _instance;
            if (plugin?._session == null) return;
            try { plugin._session.ObservedHit(__instance, __0); }
            catch (Exception ex) { plugin.FailSession(ex); }
        }

        private void Update()
        {
            if (_failed || _session == null) return;
            try
            {
                if (Application.isFocused && _settings.SilenceKey.Value.IsDown()) _session.ToggleSilence();
                if (Time.unscaledTime < _nextTick) return;
                _nextTick = Time.unscaledTime + 0.1f;
                if (!_session.Tick()) StopSession();
            }
            catch (Exception ex) { FailSession(ex); }
        }

        private void LateUpdate()
        {
            if (_settings == null || !_settings.ApplyPendingMenu()) return;
            try
            {
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(ConfigurationMenu.PluginId, out var manager))
                    ConfigurationMenu.Rebuild(manager.Instance);
            }
            catch (Exception ex)
            {
                // A menu refresh failure must not stop the voice director.
                Logger.LogWarning("Configuration menu refresh failed; reopen F12 to reload labels. " + ex.Message);
            }
        }

        private void FailSession(Exception ex)
        {
            _session?.Fault(ex);
            StopSession();
            Logger.LogError("Automatic voice stopped for this session to protect gameplay. " + ex);
        }

        private void StopSession()
        {
            VoiceSession old = _session;
            _session = null;
            old?.Dispose();
        }

        private void OnDestroy()
        {
            StopSession();
            _settings?.Dispose();
            Dialogue?.Dispose(); Dialogue = null;
            _harmony?.UnpatchSelf();
            if (_instance == this) _instance = null;
        }
    }
}
