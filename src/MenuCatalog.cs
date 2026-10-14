using System.Collections.Generic;

namespace ImmersiveVoice
{
    internal static class MenuLanguage
    {
        public const string 简体中文 = "简体中文", English = "English";
    }
    internal enum MenuGroup { General, Speech, Team, Frequency, Diagnostics, Advanced }

    // These field names/types are Configuration Manager's optional tag contract.
    // Keep storage keys independent of translated labels; no GUI assembly dependency.
    internal sealed class ConfigurationManagerAttributes
    {
        public string DispName, Description, Category;
        public bool? Browsable, IsAdvanced = false, ShowRangeAsPercent = false;
        public int Order;
    }

    internal sealed class MenuText
    {
        public readonly MenuGroup Group;
        public readonly bool Advanced;
        public readonly string ChineseName, EnglishName, ChineseHelp, EnglishHelp;
        public readonly ConfigurationManagerAttributes Attributes = new ConfigurationManagerAttributes();

        public MenuText(MenuGroup group, bool advanced, string chineseName, string englishName, string chineseHelp, string englishHelp)
        {
            Group = group; Advanced = advanced; ChineseName = chineseName; EnglishName = englishName;
            ChineseHelp = chineseHelp; EnglishHelp = englishHelp;
        }

        public void Apply(string language, bool advanced)
        {
            bool english = language == MenuLanguage.English;
            Attributes.DispName = english ? EnglishName : ChineseName;
            Attributes.Description = english ? EnglishHelp : ChineseHelp;
            Attributes.Category = MenuCatalog.Category(Group, english);
            Attributes.Browsable = !Advanced || advanced;
        }
    }

    internal static class MenuCatalog
    {
        public static string Category(MenuGroup group, bool english)
        {
            switch (group)
            {
                case MenuGroup.General: return english ? "01 General" : "01 常规";
                case MenuGroup.Speech: return english ? "02 Character speech" : "02 角色语音";
                case MenuGroup.Team: return english ? "03 Squad conversation" : "03 队友交流";
                case MenuGroup.Frequency: return english ? "04 Frequency and quiet" : "04 频率与静默";
                case MenuGroup.Diagnostics: return english ? "05 Troubleshooting" : "05 问题排查";
                default: return english ? "06 Advanced" : "06 高级设置";
            }
        }

        // A fresh catalog per Settings instance: mutable UI tags must never leak
        // across config files, tests, or a reloaded plugin instance.
        public static Dictionary<string, MenuText> Create() => new Dictionary<string, MenuText>
        {
            [nameof(Settings.Language)] = new MenuText(MenuGroup.General, false, "语言 / Language", "语言 / Language",
                "切换本 Mod 菜单的语言。不会改变角色声线或游戏语言。",
                "Choose the language of this mod menu. This does not change character voices or the game language."),
            [nameof(Settings.ShowAdvanced)] = new MenuText(MenuGroup.General, false, "显示高级设置", "Show advanced settings",
                "显示细分功能和时间参数。通常保留默认值即可；隐藏选项不会重置已有设置。",
                "Show individual features and timing controls. Defaults suit most players. Hiding these options does not reset them."),
            [nameof(Settings.Enabled)] = new MenuText(MenuGroup.General, false, "启用自动语音", "Enable automatic speech",
                "让你的角色根据正在发生的事情自动说话。关闭后仍可手动使用语音。",
                "Let your character speak in response to what is happening. Manual voice commands still work when disabled."),
            [nameof(Settings.SilenceKey)] = new MenuText(MenuGroup.General, false, "本局静默快捷键", "Raid mute hotkey",
                "按一下暂停本 Mod 的自动语音，再按一下恢复。只对本局生效，不影响手动语音。",
                "Press to mute or unmute this mod for the current raid. Manual speech is unaffected."),
            [nameof(Settings.AutoContext)] = new MenuText(MenuGroup.Speech, false, "动作与环境语音", "Action and interaction callouts",
                "换弹、缺弹、武器故障、手雷和环境互动时尝试相应语音。缺少匹配音效时保持安静。",
                "Allow callouts for reloads, ammo, weapon malfunctions, grenades and interactions. Missing voice lines stay silent."),
            [nameof(Settings.Visual)] = new MenuText(MenuGroup.Speech, false, "敌情通报", "Enemy callouts",
                "看到敌人、丢失视野或亲眼确认敌人倒下时通报。不会隔墙知道敌人死亡。",
                "React to visible enemies, lost visual contact and witnessed deaths. No death reports through walls."),
            [nameof(Settings.DamageReactions)] = new MenuText(MenuGroup.Speech, false, "受伤语音", "Injury callouts",
                "受伤、出血或骨折时表达当前伤情，避免连续重复播报。",
                "React to injuries, bleeding or fractures without repeating every wound."),
            [nameof(Settings.Healing)] = new MenuText(MenuGroup.Speech, false, "治疗语音", "Treatment callouts",
                "使用医疗品时尝试治疗语音。治疗后若仍有明显伤情，可能再提醒一次。声线没有对应台词时不会发声。",
                "Try a treatment line when using medical items. A remaining serious wound may prompt a follow-up. Voices without a matching line stay silent."),
            [nameof(Settings.Combat)] = new MenuText(MenuGroup.Speech, false, "战斗喊话", "Combat chatter",
                "交火时偶尔喊话或挑衅，也允许符合情境的战后嘲讽。关闭不会关闭敌情通报。",
                "Allow occasional combat shouts and appropriate post-fight taunts. Enemy callouts remain separate."),
            [nameof(Settings.Idle)] = new MenuText(MenuGroup.Speech, false, "平静时自言自语", "Calm chatter",
                "非交火时偶尔低声自言自语。没有匹配音效时保持安静。",
                "Allow occasional muttering outside combat. Missing voice lines stay silent."),
            [nameof(Settings.SquadCoordination)] = new MenuText(MenuGroup.Team, false, "队友间自动交流", "Automatic squad conversation",
                "Fika 最多4人：根据实际跟随、掩护、推进、后撤等行动主动交流。只让自己的角色说话，不会控制队友。",
                "In Fika squads of up to four, speak about following, covering, advancing and retreating when the actions fit. This does not control teammates."),
            [nameof(Settings.TeamReplies)] = new MenuText(MenuGroup.Team, false, "回应附近队友", "Reply to nearby teammates",
                "听到队友的行动语音，且自己正在配合时回应。不会每听到一句话都回答。",
                "Reply to a nearby teammate when your own actions match their request. Not every line needs an answer."),
            [nameof(Settings.Gap)] = new MenuText(MenuGroup.Frequency, false, "普通语音最短间隔（秒）", "Ordinary speech gap (seconds)",
                "数值越大，普通语音越少。敌情通报和紧急反应仍可及时发声，但不会打断正在播放的正常语句。",
                "Higher values reduce ordinary chatter. Tactical and urgent callouts can respond sooner without interrupting normal speech already playing."),
            [nameof(Settings.CalmReloadChance)] = new MenuText(MenuGroup.Frequency, false, "平静时换弹说话概率（%）", "Calm reload chance (%)",
                "非交火时，每次换弹尝试说话的概率。50表示约一半动作尝试；100仍可能因正在说话或缺音效而安静。",
                "Chance to attempt a line on each calm reload. 50 means roughly half of reloads; even 100 still respects busy speech and missing audio."),
            [nameof(Settings.RespectStealth)] = new MenuText(MenuGroup.Frequency, false, "潜行时减少说话", "Reduce speech while sneaking",
                "蹲伏、趴下或慢走时停止闲聊和普通战术交流；紧急反应仍可发声。普通动作与环境语音另由高级设置控制。",
                "Suppress chatter and ordinary squad conversation while crouching, prone or moving slowly. Urgent reactions can still speak. Action and interaction callouts have a separate advanced control."),
            [nameof(Settings.ManualPause)] = new MenuText(MenuGroup.Frequency, false, "手动喊话后安静多久（秒）", "Quiet time after manual speech (seconds)",
                "手动使用语音后，自动语音暂停这么长时间，给你的交流让位。",
                "Pause automatic speech for this long after you use a manual voice command."),
            [nameof(Settings.Recording)] = new MenuText(MenuGroup.Diagnostics, false, "保存排查日志", "Save diagnostic logs",
                "建议保持开启，方便排查漏说、重复或延迟。日志保存在 BepInEx/ImmersiveVoiceLogs，保留最近10份；不记录昵称、坐标或音频。",
                "Keep enabled to investigate missing, repeated or delayed lines. Logs are saved in BepInEx/ImmersiveVoiceLogs; the latest 10 are kept. No nicknames, coordinates or audio are recorded."),
            [nameof(Settings.Director)] = new MenuText(MenuGroup.Advanced, true, "启用事件语音调度", "Enable event reactions",
                "管理动作、敌情、伤情和队友交流。关闭会停用这些事件反应，普通环境提示和闲聊仍可保留；通常应开启。",
                "Schedule action, enemy, injury and squad reactions. Disabling stops these event reactions; ordinary context hints and chatter can remain. Normally leave enabled."),
            [nameof(Settings.FocusOnly)] = new MenuText(MenuGroup.Advanced, true, "切出游戏时暂停", "Pause when game loses focus",
                "切换到其他窗口时暂停自动语音。",
                "Pause automatic speech while another window has focus."),
            [nameof(Settings.Cooperation)] = new MenuText(MenuGroup.Advanced, true, "自动向人物打招呼", "Automatic greetings",
                "允许环境互动提示触发合作招呼，原版可能同时做友好手势。关闭可避免自动打招呼。",
                "Allow contextual cooperation greetings, which may include a friendly gesture. Disable to avoid automatic greetings."),
            [nameof(Settings.ContextInStealth)] = new MenuText(MenuGroup.Advanced, true, "潜行时允许普通动作语音", "Allow ordinary action callouts while sneaking",
                "允许潜行时的普通环境提示，以及平静或战后换弹语音。紧急反应本就可以发声。",
                "Allow ordinary context hints and calm or post-combat reload lines while sneaking. Urgent reactions are already allowed."),
            [nameof(Settings.FriendlyDown)] = new MenuText(MenuGroup.Advanced, true, "目击队友阵亡时反应", "React to witnessed teammate deaths",
                "亲眼确认曾见过的队友阵亡时尝试语音。可救倒地、掉线和撤离不算阵亡。",
                "React to a witnessed death of a previously seen teammate. Revivable knockdowns, disconnects and extraction do not count."),
            [nameof(Settings.MedicalRequests)] = new MenuText(MenuGroup.Advanced, true, "缺药时向队友求助", "Ask teammates for medicine",
                "Fika 中重伤或重出血、确认自己没药且附近有可见队友时尝试求药。不会查看或转移队友物品。",
                "In Fika, ask a visible nearby teammate for medicine when badly wounded or heavily bleeding and confirmed out of usable medicine. No teammate inventory access or automatic transfers."),
            [nameof(Settings.ReactionMin)] = new MenuText(MenuGroup.Advanced, true, "环境提示反应时间下限（秒）", "Context response delay minimum (seconds)",
                "普通环境提示出现后，随机等待时间的下限。紧急手雷警告使用更短延迟。",
                "Minimum random delay after an ordinary context hint appears. Urgent grenade warnings respond sooner."),
            [nameof(Settings.ReactionMax)] = new MenuText(MenuGroup.Advanced, true, "环境提示反应时间上限（秒）", "Context response delay maximum (seconds)",
                "普通环境提示出现后，随机等待时间的上限。上下限填反时会自动按大小取值。",
                "Maximum random delay after an ordinary context hint. Reversed minimum and maximum values are handled automatically."),
            [nameof(Settings.Repeat)] = new MenuText(MenuGroup.Advanced, true, "同类环境提示间隔（秒）", "Repeated context hint gap (seconds)",
                "减少同类普通环境语音重复。换弹、伤情和同一物体另有间隔设置。",
                "Limit repeated ordinary context lines. Reloads, injuries and individual objects have separate gaps."),
            [nameof(Settings.CombatMin)] = new MenuText(MenuGroup.Advanced, true, "战斗喊话间隔下限（秒）", "Combat chatter interval minimum (seconds)",
                "持续交火时两次喊话尝试之间的最短等待；其他语音播放后重新计时。",
                "Minimum interval between combat chatter attempts during an ongoing fight. Other speech restarts the timer."),
            [nameof(Settings.CombatMax)] = new MenuText(MenuGroup.Advanced, true, "战斗喊话间隔上限（秒）", "Combat chatter interval maximum (seconds)",
                "持续交火时两次喊话尝试之间的最长随机等待。",
                "Maximum random interval between combat chatter attempts during an ongoing fight."),
            [nameof(Settings.CombatChance)] = new MenuText(MenuGroup.Advanced, true, "战斗喊话概率（%）", "Combat chatter chance (%)",
                "每次战斗喊话机会尝试说话的概率；不影响换弹、敌情和受伤反应。",
                "Chance to speak at each combat chatter opportunity. Does not change reload, enemy or injury reactions."),
            [nameof(Settings.IdleMin)] = new MenuText(MenuGroup.Advanced, true, "平静闲聊间隔下限（秒）", "Calm chatter interval minimum (seconds)",
                "平静时两次闲聊机会之间的最短随机等待。",
                "Minimum random interval between calm chatter opportunities."),
            [nameof(Settings.IdleMax)] = new MenuText(MenuGroup.Advanced, true, "平静闲聊间隔上限（秒）", "Calm chatter interval maximum (seconds)",
                "平静时两次闲聊机会之间的最长随机等待。",
                "Maximum random interval between calm chatter opportunities."),
            [nameof(Settings.SightRange)] = new MenuText(MenuGroup.Advanced, true, "敌情观察距离（米）", "Enemy observation distance (metres)",
                "在此距离内检查实际看见的敌人。增加距离不会获得隔墙信息，也不会扫描全地图。",
                "Check actually visible enemies within this distance. Increasing it does not reveal enemies through walls or scan the whole map."),
            [nameof(Settings.ReloadRepeat)] = new MenuText(MenuGroup.Advanced, true, "换弹语音最短间隔（秒）", "Reload callout gap (seconds)",
                "不同换弹动作之间的最短语音间隔。同一次换弹不会重复判断。",
                "Minimum gap between callouts for separate reload actions. Each reload is considered only once."),
            [nameof(Settings.RecoveryReloadChance)] = new MenuText(MenuGroup.Advanced, true, "战后换弹说话概率（%）", "Post-combat reload chance (%)",
                "普通战后换弹的说话概率；激烈交火后至少75%。交火中的换弹不受此概率限制。",
                "Chance to speak on a post-combat reload; at least 75% after an intense fight. Reloads during combat do not use this chance limit."),
            [nameof(Settings.InjuryRepeat)] = new MenuText(MenuGroup.Advanced, true, "伤情语音间隔（秒）", "Injury callout gap (seconds)",
                "减少重复伤情播报。急性伤势明显恶化时仍可及时提醒，不代表同一伤口到时必定重复。",
                "Limit repeated wound reports. An acutely worsening wound can still prompt a new warning; an unchanged wound need not repeat when this timer ends."),
            [nameof(Settings.ObjectRepeat)] = new MenuText(MenuGroup.Advanced, true, "同一物体语音间隔（秒）", "Same-object callout gap (seconds)",
                "对同一物品、尸体或门重复说话前的最短等待。",
                "Minimum wait before commenting on the same item, corpse or door again."),
            [nameof(Settings.PostKillChance)] = new MenuText(MenuGroup.Advanced, true, "战后嘲讽概率（%）", "Post-fight taunt chance (%)",
                "符合条件的单人战斗结束后，选择嘲讽的概率；0关闭嘲讽。近处有Fika队友时优先击倒通报，受伤等情况仍会限制发声。",
                "Chance to choose a taunt after an eligible solo fight; 0 disables taunts. Nearby Fika teammates take priority for enemy-down reports. Wounds and other conditions still limit speech."),
            [nameof(Settings.Debug)] = new MenuText(MenuGroup.Advanced, true, "向主日志写入详细排查信息", "Verbose output in main log",
                "通常关闭即可。开启后也将详细事件写入 BepInEx/LogOutput.log；不影响上面的独立排查日志。",
                "Normally leave disabled. Also write detailed events to BepInEx/LogOutput.log, separately from the diagnostic log option above."),
        };
    }
}
