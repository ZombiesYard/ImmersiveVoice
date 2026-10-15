using System;
using System.Diagnostics;
using System.Text;

namespace ImmersiveVoice
{
    // Persist useful call sites without exception messages, argument values,
    // profile IDs or source-file paths. Only used on failures, never per tick.
    internal static class FaultDetails
    {
        public static string Format(Exception error)
        {
            try
            {
                var text = new StringBuilder();
                for (int depth = 0; error != null && depth < 3; depth++, error = error.InnerException)
                {
                    if (depth > 0) text.Append(" Inner="); else text.Append("Reason=");
                    text.Append(error.GetType().Name).Append(" Stack=");
                    var stack = new StackTrace(error, false);
                    int count = Math.Min(stack.FrameCount, 16);
                    if (count == 0) text.Append("Unavailable");
                    for (int i = 0; i < count; i++)
                    {
                        if (i > 0) text.Append("|");
                        var frame = stack.GetFrame(i);
                        var method = frame?.GetMethod();
                        text.Append(method?.DeclaringType?.FullName ?? "Unknown").Append(".")
                            .Append(method?.Name ?? "Unknown").Append("@").Append(frame?.GetILOffset() ?? -1);
                        if (text.Length > 2200) return text.ToString(0, 2200) + " StackTruncated=True";
                    }
                    if (stack.FrameCount > count) text.Append("|...");
                }
                return text.ToString().Replace('\r', ' ').Replace('\n', ' ');
            }
            catch { return "Reason=FaultDetailsUnavailable"; }
        }
    }
}
