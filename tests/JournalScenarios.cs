using System.Globalization;
using ImmersiveVoice;

internal static class JournalScenarios
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ImmersiveVoiceTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var disabled = new VoiceJournal(Path.Combine(root, "disabled")))
            {
                disabled.Enable(false); disabled.Record(1, "Event=NoOutput"); disabled.Dispose();
                check(!Directory.Exists(Path.Combine(root, "disabled")), "disabled diagnostic recording performs no file creation");
            }
            var journal = new VoiceJournal(Path.Combine(root, "normal"));
            journal.Enable(true);
            int first = journal.Request(), second = journal.Request();
            check(first > 0 && second > first, "request IDs are monotonic within the diagnostic session");
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                journal.Record(1.25f, "Event=Candidate Request=1 Reason=New\nInjected=Ignored");
                journal.Record(2, "Event=Attempt Request=1");
                journal.Record(2.1f, "Event=Played Request=1");
            }
            finally { CultureInfo.CurrentCulture = culture; }
            journal.Dispose(); journal.Dispose();
            check(journal.WaitForExit(5000), "disposing the recorder drains pending records on its background worker");
            string[] lines = File.ReadAllLines(journal.FilePath);
            check(lines.Length == 4 && lines[0].Contains("Time=1.250") && lines[0].Contains("Reason=New Injected=Ignored"),
                "journal timestamps are locale independent and embedded newlines cannot forge log records");
            check(lines[0].Contains("Seq=1") && lines[1].Contains("Seq=2") && lines[2].Contains("Seq=3")
                && lines[3].Contains("Event=JournalClosed"), "record order and a close marker distinguish complete files from interrupted ones");
            journal.Record(3, "Event=TooLate");
            check(File.ReadAllLines(journal.FilePath).Length == 4, "late callbacks after disposal cannot reopen the journal");

            journal = new VoiceJournal(Path.Combine(root, "fault")); journal.Enable(true);
            try { throw new NullReferenceException("private-message-token\nEvent=Injected"); }
            catch (Exception error) { journal.Record(4, "Event=Fault Component=Session " + FaultDetails.Format(error)); }
            journal.Dispose();
            check(journal.WaitForExit(5000), "session fault details survive disposal in the independent journal");
            string fault = File.ReadAllText(journal.FilePath);
            check(fault.Contains("Reason=NullReferenceException") && fault.Contains("JournalScenarios.Run")
                && !fault.Contains("private-message-token") && !fault.Contains("Event=Injected") && !fault.Contains(root),
                "persistent fault call sites exclude exception messages, arbitrary records and file paths");

            journal = new VoiceJournal(Path.Combine(root, "burst")); journal.Enable(true);
            const int count = 20000;
            for (int i = 0; i < count; i++) journal.Record(i, "Event=Burst Index=" + i);
            journal.Dispose();
            check(journal.WaitForExit(5000), "bounded burst recording drains without blocking the producer on disk");
            lines = File.ReadAllLines(journal.FilePath);
            int written = lines.Count(l => l.Contains("Event=Burst"));
            check(written + journal.Dropped == count && !journal.Failed,
                "overflow is explicitly counted: every burst record was either written or reported dropped");
            int previous = -1;
            bool ordered = true;
            foreach (var line in lines.Where(l => l.Contains("Event=Burst")))
            {
                int index = int.Parse(line.Split("Index=")[1]);
                if (index <= previous) ordered = false;
                previous = index;
            }
            check(ordered, "queue pressure does not reorder surviving diagnostic records");

            journal = new VoiceJournal(Path.Combine(root, "limit")); journal.Enable(true);
            string payload = "Event=Large Text=" + new string('伤', 3500);
            for (int batch = 0; batch < 1000 && journal.Enabled; batch++)
            {
                for (int i = 0; i < 32; i++) journal.Record(batch, payload);
                Thread.Sleep(2);
            }
            journal.Dispose(); check(journal.WaitForExit(5000), "large UTF-8 diagnostics finish within the bounded file policy");
            check(new FileInfo(journal.FilePath).Length <= VoiceJournal.FileLimit && !journal.Failed
                && File.ReadAllText(journal.FilePath).Contains("Capped=True"),
                "the 4 MiB limit measures encoded bytes, reports truncation, and stops further recording");

            string retention = Path.Combine(root, "retention"); Directory.CreateDirectory(retention);
            for (int i = 0; i < 15; i++) File.WriteAllText(Path.Combine(retention, $"voice-20000101-{i:D6}-old.log"), "old");
            File.WriteAllText(Path.Combine(retention, "unrelated.log"), "keep");
            journal = new VoiceJournal(retention); journal.Enable(true); journal.Record(0, "Event=Start"); journal.Dispose();
            check(journal.WaitForExit(5000) && Directory.GetFiles(retention, "voice-*.log").Length == VoiceJournal.RetainedFiles,
                "session retention keeps only the ten most recent owned log files");
            check(File.ReadAllText(Path.Combine(retention, "unrelated.log")) == "keep", "retention leaves unrelated files untouched");

            string blocked = Path.Combine(root, "is-a-file"); File.WriteAllText(blocked, "obstruction");
            int errors = 0;
            journal = new VoiceJournal(blocked, _ => Interlocked.Increment(ref errors));
            journal.Enable(true); journal.Record(0, "Event=CannotWrite"); journal.Dispose();
            check(journal.WaitForExit(5000) && journal.Failed && errors == 1,
                "an unwritable destination disables only diagnostics and reports one error without throwing into gameplay");
        }
        finally { Directory.Delete(root, true); }
    }
}
