using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using ImmersiveVoice;

internal static class Program
{
    private static int _checks;
    private static void Check(bool success, string label)
    {
        if (!success) throw new Exception(label);
        _checks++; Console.WriteLine("PASS: " + label);
    }
    private static ConfigurationManagerAttributes Tag(ConfigEntryBase entry)
        => entry.Description.Tags.OfType<ConfigurationManagerAttributes>().Single();
    private static string Stored(ConfigEntryBase entry)
        => entry.BoxedValue is float number ? number.ToString(CultureInfo.InvariantCulture) : entry.BoxedValue.ToString();

    public static void Main(string[] args)
    {
        string temp = Path.Combine(Path.GetTempPath(), "ImmersiveVoice-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try { Run(temp, args[0]); }
        finally { Directory.Delete(temp, true); }
        Console.WriteLine("SUCCESS: " + _checks + " real BepInEx/config-menu checks; no Unity window or gameplay test.");
    }

    private static void Run(string temp, string managerPath)
    {
        var baseline = File.ReadAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "legacy-settings.tsv"))
            .Where(line => !line.StartsWith("#") && line.Length > 0).Select(line => line.Split('\t')).ToArray();
        string path = Path.Combine(temp, "legacy.cfg");
        var text = new StringBuilder();
        foreach (var group in baseline.GroupBy(row => row[1]))
        {
            text.AppendLine("[" + group.Key + "]");
            foreach (var row in group) text.AppendLine(row[2] + " = " + row[5]);
        }
        text.AppendLine("[Unknown preserved section]").AppendLine("Future option = keep-me");
        File.WriteAllText(path, text.ToString());
        var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
        using (var settings = new Settings(config))
        {
            Check(config.Count == 38, "upgrade keeps 36 legacy settings and adds only two menu preferences");
            foreach (var row in baseline)
            {
                var entry = config[new ConfigDefinition(row[1], row[2])];
                Check(row[3] == "float" ? (float)entry.BoxedValue == float.Parse(row[5], CultureInfo.InvariantCulture)
                    : Stored(entry).Equals(row[5], StringComparison.OrdinalIgnoreCase), row[0] + " retains its old storage key and custom value");
                string originalDefault = entry.DefaultValue is float n ? n.ToString(CultureInfo.InvariantCulture) : entry.DefaultValue.ToString();
                Check(originalDefault.Equals(row[4], StringComparison.OrdinalIgnoreCase), row[0] + " retains its original default");
                if (row[3] == "float")
                {
                    var range = (AcceptableValueRange<float>)entry.Description.AcceptableValues;
                    Check(range.MinValue == float.Parse(row[6], CultureInfo.InvariantCulture)
                        && range.MaxValue == float.Parse(row[7], CultureInfo.InvariantCulture), row[0] + " retains its allowed range");
                }
            }
            Check(config.Count(pair => Tag(pair.Value).Browsable == true) == 17, "default menu exposes only 17 player-facing options");
            Check(!settings.ApplyPendingMenu(), "initial menu has no pending per-frame rebuild");
            var values = config.Where(p => p.Key.Section != "00 Menu").ToDictionary(p => p.Key, p => Stored(p.Value));
            settings.Language.Value = MenuLanguage.English;
            settings.ShowAdvanced.Value = true;
            Check(Tag(settings.Enabled).DispName == "启用自动语音", "menu callbacks do not mutate labels during the current GUI draw");
            Check(settings.ApplyPendingMenu() && !settings.ApplyPendingMenu(), "language and visibility changes coalesce into one deferred refresh");
            Check(Tag(settings.Enabled).DispName == "Enable automatic speech" && Tag(settings.Enabled).Category == "01 General",
                "English changes both the displayed name and category");
            Check(config.All(p => Tag(p.Value).Browsable == true), "show advanced reveals every legacy control");
            Check(config.All(p => !string.IsNullOrWhiteSpace(Tag(p.Value).Description)
                && Tag(p.Value).IsAdvanced == false && Tag(p.Value).ShowRangeAsPercent == false),
                "all controls have help and explicit visibility and raw-unit slider metadata");
            Check(values.All(p => Stored(config[p.Key]) == p.Value), "language and visibility never alter gameplay values");

            // Use the installed Configuration Manager's actual tag reader,
            // rather than a fixture that merely duplicates its reflection rules.
            Assembly manager = Assembly.LoadFrom(managerPath);
            Type settingType = manager.GetType("ConfigurationManager.ConfigSettingEntry", true);
            var ctor = settingType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(c => c.GetParameters().Length == 2);
            foreach (var pair in config)
            {
                object view = ctor.Invoke(new object[] { pair.Value, null });
                string Property(string name) => settingType.GetProperty(name).GetValue(view)?.ToString();
                Check(Property("DispName") == Tag(pair.Value).DispName && Property("Description") == Tag(pair.Value).Description
                    && Property("Category") == Tag(pair.Value).Category && Property("Browsable") == "True",
                    pair.Key.Key + " metadata is accepted by the installed F12 manager");
            }
            object languageView = ctor.Invoke(new object[] { settings.Language, null });
            var choices = (object[])settingType.GetProperty("AcceptableValues").GetValue(languageView);
            Check(choices.Select(v => v.ToString()).SequenceEqual(new[] { MenuLanguage.简体中文, MenuLanguage.English }),
                "the real menu receives a two-language dropdown, not an unrestricted text box");
            settings.Language.Value = "unsupported-language";
            Check(settings.Language.Value == MenuLanguage.简体中文, "invalid saved language choices fall back to the supported default");
            settings.ApplyPendingMenu();
            settings.Combat.Value = !settings.Combat.Value;
            Check(!settings.ApplyPendingMenu(), "ordinary gameplay settings do not rebuild the menu");
            settings.Combat.Value = !settings.Combat.Value;
            settings.ShowAdvanced.Value = false; settings.ApplyPendingMenu();
            object hidden = ctor.Invoke(new object[] { settings.ReactionMin, null });
            Check(settingType.GetProperty("Browsable").GetValue(hidden).Equals(false), "installed manager honors hidden advanced controls");
            settings.Language.Value = MenuLanguage.简体中文; settings.ApplyPendingMenu();
            Check(Tag(settings.Visual).DispName == "敌情通报" && Tag(settings.Visual).Category == "02 角色语音",
                "switching back restores Chinese labels and categories");
            config.Save();
            Check(File.ReadAllText(path).Contains("keep-me"), "saving preserves unknown pre-existing config entries");
            var roundtrip = new ConfigFile(path, false) { SaveOnConfigSet = false };
            using (var reopened = new Settings(roundtrip))
            {
                Check(values.All(p => Stored(roundtrip[p.Key]) == p.Value), "all legacy values survive a save and reload");
                settings.Language.Value = MenuLanguage.English; settings.ShowAdvanced.Value = true; settings.ApplyPendingMenu();
                Check(Tag(reopened.Enabled).DispName == "启用自动语音" && Tag(reopened.ReactionMin).Browsable == false,
                    "menu metadata is isolated between Settings instances");
            }
            settings.Language.Value = MenuLanguage.English; settings.ShowAdvanced.Value = true; config.Save();
            var englishFile = new ConfigFile(path, false) { SaveOnConfigSet = false };
            using (var english = new Settings(englishFile))
                Check(Tag(english.Enabled).DispName == "Enable automatic speech" && Tag(english.ReactionMin).Browsable == true,
                    "language and advanced visibility persist across restart");

            settings.Dispose(); settings.Language.Value = MenuLanguage.简体中文;
            Check(!settings.ApplyPendingMenu(), "disposing detaches the menu callbacks");
        }
        Check(!ConfigurationMenu.Rebuild(null) && !ConfigurationMenu.Rebuild(new object()), "missing or unsupported menu integration stays optional");
        var bridge = new ManagerFixture();
        Check(ConfigurationMenu.Rebuild(bridge) && bridge.Rebuilds == 1, "a compatible menu gets exactly one rebuild per request");
        Check(!ConfigurationMenu.Rebuild(new WrongSignature()), "an incompatible rebuild signature is not invoked");
    }

    private sealed class ManagerFixture { public int Rebuilds; public void BuildSettingList() => Rebuilds++; }
    private sealed class WrongSignature { public bool BuildSettingList() => throw new Exception("Must not invoke"); }
}
