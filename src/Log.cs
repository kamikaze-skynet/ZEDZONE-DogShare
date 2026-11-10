using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace DogShare
{
    // DogShare's log. Every line goes to BepInEx\LogOutput.log and to its own file,
    // BepInEx\DogShare.log (rewritten each launch), with a timestamp and a category:
    //   [start] loading and patches      [role] offline / host / guest
    //   [host]  give, take back, relink   [req]  guest requests the host handled
    //   [send]  host replies / gear sent  [guest] mirrors on a guest's PC
    //   [act]   guest actions sent        [item] items moving (feed, gear, saddlebag)
    //   [status] summary every 30 s       [error] anything that failed
    internal static class DLog
    {
        static StreamWriter _file;
        static readonly Dictionary<string, string> _last = new Dictionary<string, string>();
        static readonly Dictionary<string, float> _next = new Dictionary<string, float>();
        static readonly HashSet<string> _once = new HashSet<string>();
        internal static bool Verbose = true;

        internal static void Open(string version)
        {
            try
            {
                var path = Path.Combine(Paths.BepInExRootPath, "DogShare.log");
                _file = new StreamWriter(path, false) { AutoFlush = true };
                _file.WriteLine($"DogShare {version} log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception e) { Plugin.L?.LogWarning("Couldn't open DogShare.log: " + e.Message); }
        }

        static void Write(string level, string cat, string msg)
        {
            string line = $"[{cat}] {msg}";
            try
            {
                if (level == "ERROR") Plugin.L?.LogError(line);
                else if (level == "WARN") Plugin.L?.LogWarning(line);
                else Plugin.L?.LogInfo(line);
            }
            catch { }
            try { _file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level,-5} {line}"); } catch { }
        }

        internal static void Info(string cat, string msg) => Write("INFO", cat, msg);
        internal static void Warn(string cat, string msg) => Write("WARN", cat, msg);
        internal static void Error(string cat, string msg, Exception e = null)
            => Write("ERROR", cat, e == null ? msg : $"{msg}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
        internal static void Debug(string cat, string msg) { if (Verbose) Write("DEBUG", cat, msg); }

        // Only when the message for this key changes (for things checked every few seconds).
        internal static void Change(string key, string cat, string msg)
        {
            if (_last.TryGetValue(key, out var old) && old == msg) return;
            _last[key] = msg;
            Info(cat, msg);
        }

        // At most once per `seconds` for this key.
        internal static void Throttle(string key, float seconds, string cat, string msg)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_next.TryGetValue(key, out var t) && now < t) return;
            _next[key] = now + seconds;
            Info(cat, msg);
        }

        // Error, but at most once per `seconds` for this key (for things retried every few seconds).
        internal static void ErrorThrottled(string key, float seconds, string cat, string msg, Exception e = null)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_next.TryGetValue("E|" + key, out var t) && now < t) return;
            _next["E|" + key] = now + seconds;
            Error(cat, msg, e);
        }

        internal static void Once(string key, string cat, string msg)
        {
            if (_once.Add(key)) Info(cat, msg);
        }

        internal static string S(string id) => Proto.Short(id);
    }
}
