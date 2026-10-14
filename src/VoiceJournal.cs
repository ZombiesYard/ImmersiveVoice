using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ImmersiveVoice
{
    // The worker receives strings only. It never reads Unity, EFT or settings.
    // Bounded queue, bounded file and bounded retention; I/O failure only stops diagnostics.
    internal sealed class VoiceJournal : IDisposable
    {
        public const int QueueLimit = 512, FileLimit = 4 * 1024 * 1024, RetainedFiles = 10;
        private readonly object _gate = new object();
        private readonly Queue<string> _pending = new Queue<string>(QueueLimit);
        private readonly string _directory;
        private readonly Action<string> _error;
        private Thread _worker;
        private volatile bool _enabled, _closed, _failed, _capped;
        private int _sequence, _request, _dropped;
        public readonly string Session = Guid.NewGuid().ToString("N").Substring(0, 12);
        public bool Enabled => _enabled && !_closed && !_failed && !_capped;
        public string FilePath { get; private set; }
        public int Dropped => Volatile.Read(ref _dropped);
        public bool Failed => _failed;
        public VoiceJournal(string directory, Action<string> error = null) { _directory = directory; _error = error; }
        public int Request() => Interlocked.Increment(ref _request);
        public void Enable(bool enabled)
        {
            if (_closed) return;
            _enabled = enabled;
            if (!Enabled || _worker != null) return;
            try
            {
                FilePath = Path.Combine(_directory, "voice-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Session + ".log");
                _worker = new Thread(WriteLoop) { IsBackground = true, Name = "ImmersiveVoice diagnostics" };
                _worker.Start();
            }
            catch (Exception ex) { Fail(ex); }
        }
        public void Record(float time, string fields)
        {
            if (!Enabled) return;
            try
            {
                if (fields.Length > 3000) fields = fields.Substring(0, 3000) + " Truncated=True";
                fields = fields.Replace('\r', ' ').Replace('\n', ' ');
                lock (_gate)
                {
                    if (_closed) return;
                    if (_pending.Count >= QueueLimit) { Interlocked.Increment(ref _dropped); return; }
                    string line = "[AutoVoice] Session=" + Session + " Seq=" + (++_sequence)
                        + " Time=" + time.ToString("F3", CultureInfo.InvariantCulture) + " " + fields;
                    _pending.Enqueue(line);
                    Monitor.Pulse(_gate);
                }
            }
            catch (Exception ex) { Fail(ex); }
        }
        private void WriteLoop()
        {
            try
            {
                Directory.CreateDirectory(_directory);
                string[] old = Directory.GetFiles(_directory, "voice-*.log");
                Array.Sort(old, StringComparer.Ordinal);
                for (int i = 0; i <= old.Length - RetainedFiles; i++)
                { try { File.Delete(old[i]); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                using (var stream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 8192))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 8192))
                {
                    int bytes = 0, written = 0, reportedDrops = 0;
                    var flush = System.Diagnostics.Stopwatch.StartNew();
                    while (true)
                    {
                        string line = null;
                        lock (_gate) { if (_pending.Count > 0) line = _pending.Dequeue(); }
                        if (line != null)
                        {
                            int length = Encoding.UTF8.GetByteCount(line) + 2;
                            if (bytes + length > FileLimit - 1024) { _capped = true; break; }
                            writer.WriteLine(line); bytes += length; written++;
                        }
                        if (flush.ElapsedMilliseconds >= 1000)
                        {
                            int dropped = Dropped;
                            if (dropped != reportedDrops)
                            {
                                string health = "[AutoVoice] Session=" + Session + " Event=JournalHealth Dropped=" + dropped;
                                writer.WriteLine(health); bytes += Encoding.UTF8.GetByteCount(health) + 2; reportedDrops = dropped;
                            }
                            writer.Flush(); flush.Restart();
                        }
                        if (line == null)
                        {
                            lock (_gate)
                            {
                                if (_pending.Count == 0)
                                {
                                    if (_closed) break;
                                    Monitor.Wait(_gate, 1000);
                                }
                            }
                        }
                    }
                    writer.WriteLine("[AutoVoice] Session=" + Session + " Event=JournalClosed Records=" + written
                        + " Dropped=" + Dropped + " Capped=" + _capped);
                }
            }
            catch (Exception ex) { Fail(ex); }
            finally { lock (_gate) _pending.Clear(); }
        }
        private void Fail(Exception ex)
        {
            _failed = true;
            try { _error?.Invoke("Voice diagnostic recording stopped: " + ex.Message); } catch { }
        }
        public void Dispose()
        {
            lock (_gate) { _enabled = false; _closed = true; Monitor.PulseAll(_gate); }
            // Never wait for the disk from the game thread; the background worker drains it.
        }
        internal bool WaitForExit(int milliseconds) => _worker == null || _worker.Join(milliseconds);
    }
}
