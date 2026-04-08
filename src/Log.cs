using System;

namespace FluxxField.DefLoadCache
{
    /// <summary>
    /// Thin wrapper around Verse.Log that prefixes every message with [DefLoadCache]
    /// and falls back to Console.WriteLine if Verse.Log is not yet initialized.
    /// </summary>
    internal static class Log
    {
        private const string Prefix = "[DefLoadCache] ";

        public static void Message(string msg)
        {
            try { Verse.Log.Message(Prefix + msg); }
            catch { Console.WriteLine(Prefix + msg); }
        }

        public static void Warning(string msg)
        {
            try { Verse.Log.Warning(Prefix + msg); }
            catch { Console.WriteLine(Prefix + "WARN " + msg); }
        }

        public static void Error(string msg, Exception? ex = null)
        {
            var full = Prefix + msg + (ex != null ? "\n" + ex : "");
            try { Verse.Log.Error(full); }
            catch { Console.WriteLine("ERROR " + full); }
        }
    }
}
