using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using MyscotekDataCopier.Core;

namespace MyscotekDataCopier.UI
{
    /// <summary>
    /// The on-screen log (SPEC section 6): an <see cref="ICopyLogger"/> that may be called from any
    /// thread. Lines are queued with the time they were logged and appended to the RichTextBox on its
    /// own thread in batches - one BeginInvoke per batch, so a fast copy cannot flood the message
    /// queue - each prefixed "[HH:mm:ss] " and coloured by level, keeping the end in view. Messages
    /// arrive already indented by the engine. When the box holds more than <see cref="MaxLines"/>
    /// lines the oldest are trimmed. Nothing is appended before the box has a window handle; what is
    /// queued by then appears as soon as it has one.
    /// </summary>
    internal sealed class UiLogger : ICopyLogger
    {
        public const int DefaultMaxLines = 20000;

        private readonly RichTextBox _box;
        private readonly Action<LogLevel, string> _sink;
        private readonly ConcurrentQueue<Entry> _pending = new ConcurrentQueue<Entry>();
        private int _flushQueued;   // 1 while a Flush is queued on the box's thread
        private int _lineCount;     // lines in the box; touched on the box's thread only

        /// <param name="box">The log box.</param>
        /// <param name="sink">
        /// Also receives every <see cref="Log"/> line on the calling thread (the control mirrors errors
        /// to XrmToolBox's own log with it); <see cref="Write"/> lines are not passed on.
        /// </param>
        /// <param name="maxLines">Line cap; the oldest lines are trimmed beyond it.</param>
        public UiLogger(RichTextBox box, Action<LogLevel, string> sink = null, int maxLines = DefaultMaxLines)
        {
            _box = box ?? throw new ArgumentNullException(nameof(box));
            _sink = sink;
            MaxLines = Math.Max(10, maxLines);
            _box.HandleCreated += OnBoxHandleCreated;
        }

        public int MaxLines { get; }

        /// <summary>Lines currently in the box (read it on the box's thread).</summary>
        public int LineCount => _lineCount;

        /// <summary>Shows the line and passes it to the sink. Any thread.</summary>
        public void Log(LogLevel level, string message)
        {
            Write(level, message);
            _sink?.Invoke(level, message ?? string.Empty);
        }

        /// <summary>Shows the line only. Any thread.</summary>
        public void Write(LogLevel level, string message)
        {
            string text = (message ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            _pending.Enqueue(new Entry(DateTime.Now, level, text));
            ScheduleFlush();
        }

        /// <summary>Empties the box and drops lines not shown yet. On the box's thread.</summary>
        public void Clear()
        {
            while (_pending.TryDequeue(out _))
            {
            }
            _box.Clear();
            _lineCount = 0;
        }

        /// <summary>Appends everything queued so far. On the box's thread (normally via BeginInvoke).</summary>
        internal void Flush()
        {
            Interlocked.Exchange(ref _flushQueued, 0);
            if (_box.IsDisposed || !_box.IsHandleCreated || _pending.IsEmpty) return;

            while (_pending.TryDequeue(out Entry entry))
            {
                _box.SelectionStart = _box.TextLength;
                _box.SelectionLength = 0;
                _box.SelectionColor = ColorOf(entry.Level);
                _box.AppendText("[" + entry.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + entry.Message + "\n");
                _lineCount += 1 + CountLineBreaks(entry.Message);
            }
            if (_lineCount > MaxLines) TrimOldest();

            _box.SelectionStart = _box.TextLength;
            _box.SelectionLength = 0;
            _box.ScrollToCaret();
        }

        /// <summary>Info = WindowText, Success = ForestGreen, Warning = DarkGoldenrod, Error = Firebrick.</summary>
        internal static Color ColorOf(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Success: return Color.ForestGreen;
                case LogLevel.Warning: return Color.DarkGoldenrod;
                case LogLevel.Error: return Color.Firebrick;
                default: return SystemColors.WindowText;
            }
        }

        private void OnBoxHandleCreated(object sender, EventArgs e)
        {
            // A BeginInvoke still pending on a destroyed handle never runs: start afresh.
            Interlocked.Exchange(ref _flushQueued, 0);
            ScheduleFlush();
        }

        private void ScheduleFlush()
        {
            if (_box.IsDisposed || !_box.IsHandleCreated) return;   // OnBoxHandleCreated flushes the queue
            if (Interlocked.CompareExchange(ref _flushQueued, 1, 0) != 0) return;
            try
            {
                _box.BeginInvoke(new Action(Flush));
            }
            catch (InvalidOperationException)
            {
                // The handle went away in between (ObjectDisposedException is one of these too).
                Interlocked.Exchange(ref _flushQueued, 0);
            }
        }

        /// <summary>Removes the oldest lines, down to 90% of the cap so this does not run on every flush.</summary>
        private void TrimOldest()
        {
            int remove = _lineCount - (MaxLines - MaxLines / 10);
            if (remove <= 0) return;
            int firstKept = _box.GetFirstCharIndexFromLine(remove);   // WordWrap is off: one line per log line
            if (firstKept <= 0) return;

            // A read-only RichTextBox ignores SelectedText = "" (appending still works), so lift
            // ReadOnly for the deletion.
            bool readOnly = _box.ReadOnly;
            try
            {
                _box.ReadOnly = false;
                _box.Select(0, firstKept);
                _box.SelectedText = string.Empty;
            }
            finally
            {
                _box.ReadOnly = readOnly;
            }
            _lineCount -= remove;
        }

        private static int CountLineBreaks(string text)
        {
            int count = 0;
            foreach (char c in text)
            {
                if (c == '\n') count++;
            }
            return count;
        }

        private readonly struct Entry
        {
            public Entry(DateTime time, LogLevel level, string message)
            {
                Time = time;
                Level = level;
                Message = message;
            }

            public DateTime Time { get; }
            public LogLevel Level { get; }
            public string Message { get; }
        }
    }
}
