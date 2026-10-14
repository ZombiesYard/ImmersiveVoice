using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace ImmersiveVoice
{
    internal sealed class Settings : IDisposable
    {
        public readonly ConfigEntry<bool> Enabled, AutoContext, Combat, Idle, RespectStealth,
            ContextInStealth, Cooperation, FocusOnly, Debug;
        public readonly ConfigEntry<float> Gap, Repeat, ReactionMin, ReactionMax, CombatMin,
            CombatMax, CombatChance, IdleMin, IdleMax, ManualPause;
        public readonly ConfigEntry<KeyboardShortcut> SilenceKey;
        public readonly ConfigEntry<bool> Director, Visual, DamageReactions, Healing, TeamReplies, MedicalRequests, SquadCoordination;
        public readonly ConfigEntry<bool> Recording, FriendlyDown;
        public readonly ConfigEntry<bool> NpcReplies;
        public readonly ConfigEntry<float> NpcReplyCooldown;
        public readonly ConfigEntry<float> SightRange, ReloadRepeat, InjuryRepeat, ObjectRepeat, PostKillChance;
        public readonly ConfigEntry<float> CalmReloadChance, RecoveryReloadChance;

        public readonly ConfigEntry<string> Language;
        public readonly ConfigEntry<bool> ShowAdvanced;
        private readonly Dictionary<string, MenuText> _menu = MenuCatalog.Create();
        private volatile bool _menuPending;
        private int _menuOrder;

        public Settings(ConfigFile config)
        {
            Language = Bind(config, "00 Menu", "Language", MenuLanguage.简体中文, nameof(Language),
                new AcceptableValueList<string>(MenuLanguage.简体中文, MenuLanguage.English));
            ShowAdvanced = Bind(config, "00 Menu", "Show advanced settings", false, nameof(ShowAdvanced));
            Enabled = Bind(config, "01 General 常规", "Enabled", true, nameof(Enabled));
            SilenceKey = Bind(config, "01 General 常规", "Silence toggle", new KeyboardShortcut(UnityEngine.KeyCode.Pause), nameof(SilenceKey));
            FocusOnly = Bind(config, "01 General 常规", "Only while focused", true, nameof(FocusOnly));
            Gap = Number(config, "01 General 常规", "Minimum speech gap", 8f, 1f, 60f, nameof(Gap));
            ManualPause = Number(config, "01 General 常规", "Manual voice priority", 12f, 2f, 120f, nameof(ManualPause));
            AutoContext = Bind(config, "02 Auto Y 自动情境语音", "Enabled", true, nameof(AutoContext));
            ReactionMin = Number(config, "02 Auto Y 自动情境语音", "Reaction minimum", 0.55f, 0.1f, 5f, nameof(ReactionMin));
            ReactionMax = Number(config, "02 Auto Y 自动情境语音", "Reaction maximum", 1.15f, 0.1f, 5f, nameof(ReactionMax));
            Repeat = Number(config, "02 Auto Y 自动情境语音", "Repeat cooldown", 45f, 5f, 300f, nameof(Repeat));
            Cooperation = Bind(config, "02 Auto Y 自动情境语音", "Allow cooperation", true, nameof(Cooperation));
            ContextInStealth = Bind(config, "02 Auto Y 自动情境语音", "Allow context while sneaking", false, nameof(ContextInStealth));
            Combat = Bind(config, "03 Monologue 情境自言自语", "Combat enabled", true, nameof(Combat));
            CombatMin = Number(config, "03 Monologue 情境自言自语", "Combat interval minimum", 18f, 5f, 180f, nameof(CombatMin));
            CombatMax = Number(config, "03 Monologue 情境自言自语", "Combat interval maximum", 32f, 5f, 180f, nameof(CombatMax));
            CombatChance = Number(config, "03 Monologue 情境自言自语", "Combat chance percent", 65f, 0f, 100f, nameof(CombatChance));
            Idle = Bind(config, "03 Monologue 情境自言自语", "Calm enabled", true, nameof(Idle));
            IdleMin = Number(config, "03 Monologue 情境自言自语", "Calm interval minimum", 90f, 15f, 600f, nameof(IdleMin));
            IdleMax = Number(config, "03 Monologue 情境自言自语", "Calm interval maximum", 180f, 15f, 600f, nameof(IdleMax));
            RespectStealth = Bind(config, "03 Monologue 情境自言自语", "Respect stealth", true, nameof(RespectStealth));
            Debug = Bind(config, "04 Diagnostics 诊断", "Debug logging", false, nameof(Debug));
            Recording = Bind(config, "04 Diagnostics 诊断", "Diagnostic recording", true, nameof(Recording));
            Director = Bind(config, "05 Event director 事件语音", "Enabled", true, nameof(Director));
            Visual = Bind(config, "05 Event director 事件语音", "Visible combat reactions", true, nameof(Visual));
            DamageReactions = Bind(config, "05 Event director 事件语音", "Damage reactions", true, nameof(DamageReactions));
            Healing = Bind(config, "05 Event director 事件语音", "Healing reactions", true, nameof(Healing));
            TeamReplies = Bind(config, "05 Event director 事件语音", "Nearby teammate replies", true, nameof(TeamReplies));
            NpcReplies = Bind(config, "05 Event director 事件语音", "Reply to NPC taunts", true, nameof(NpcReplies));
            NpcReplyCooldown = Number(config, "05 Event director 事件语音", "NPC reply cooldown", 30f, 8f, 120f, nameof(NpcReplyCooldown));
            SquadCoordination = Bind(config, "05 Event director 事件语音", "Squad coordination", true, nameof(SquadCoordination));
            FriendlyDown = Bind(config, "05 Event director 事件语音", "Witnessed teammate death", true, nameof(FriendlyDown));
            MedicalRequests = Bind(config, "05 Event director 事件语音", "Medical supply requests", true, nameof(MedicalRequests));
            SightRange = Number(config, "05 Event director 事件语音", "Visual range", 60f, 10f, 100f, nameof(SightRange));
            ReloadRepeat = Number(config, "05 Event director 事件语音", "Reload cooldown", 1f, 0.5f, 10f, nameof(ReloadRepeat));
            CalmReloadChance = Number(config, "05 Event director 事件语音", "Calm reload chance percent", SemanticVoice.DefaultCalmReloadChance, 0f, 100f, nameof(CalmReloadChance));
            RecoveryReloadChance = Number(config, "05 Event director 事件语音", "Post combat reload chance percent", SemanticVoice.DefaultRecoveryReloadChance, 0f, 100f, nameof(RecoveryReloadChance));
            InjuryRepeat = Number(config, "05 Event director 事件语音", "Injury cooldown", 20f, 5f, 90f, nameof(InjuryRepeat));
            ObjectRepeat = Number(config, "05 Event director 事件语音", "Same object cooldown", 60f, 10f, 300f, nameof(ObjectRepeat));
            PostKillChance = Number(config, "05 Event director 事件语音", "Post kill taunt chance percent", 45f, 0f, 100f, nameof(PostKillChance));
            ApplyMenu();
            Language.SettingChanged += MenuChanged;
            ShowAdvanced.SettingChanged += MenuChanged;
        }

        private ConfigEntry<T> Bind<T>(ConfigFile config, string section, string key, T value, string id,
            AcceptableValueBase range = null)
        {
            MenuText text = _menu[id];
            text.Attributes.Order = 100 - _menuOrder++;
            return config.Bind(section, key, value,
                new ConfigDescription(text.ChineseHelp + "\n" + text.EnglishHelp, range, text.Attributes));
        }

        private ConfigEntry<float> Number(ConfigFile config, string section, string key,
            float value, float min, float max, string id)
            => Bind(config, section, key, value, id, new AcceptableValueRange<float>(min, max));

        private void MenuChanged(object sender, EventArgs e) => _menuPending = true;

        private void ApplyMenu()
        {
            foreach (MenuText text in _menu.Values) text.Apply(Language.Value, ShowAdvanced.Value);
        }

        // Called from LateUpdate, never inside Configuration Manager's drawing
        // or a ConfigFile callback; multiple changes coalesce into one refresh.
        public bool ApplyPendingMenu()
        {
            if (!_menuPending) return false;
            _menuPending = false;
            ApplyMenu();
            return true;
        }

        public void Dispose()
        {
            Language.SettingChanged -= MenuChanged;
            ShowAdvanced.SettingChanged -= MenuChanged;
        }
    }
}
