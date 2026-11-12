using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DogShare
{
    // Freeze detector. DogShare times its own work (its per-frame updates and the game hooks that run often)
    // and, when a frame takes longer than HitchMs, logs how long it took and how much of it was DogShare.
    // If a freeze shows "DogShare: 2 ms of 1000 ms", the freeze is something else (the game, another mod, memory cleanup).
    internal static class Hitch
    {
        const float HitchMs = 250f;
        static readonly Stopwatch _sw = Stopwatch.StartNew();
        static readonly Dictionary<string, double> _spent = new Dictionary<string, double>();
        static readonly Dictionary<string, int> _calls = new Dictionary<string, int>();
        static int _frame = -1;
        static double _last = -1;
        static int _hitches;

        internal static long Start() => _sw.ElapsedTicks;

        internal static void Stop(string what, long start)
        {
            double ms = (_sw.ElapsedTicks - start) * 1000.0 / Stopwatch.Frequency;
            Roll();
            _spent.TryGetValue(what, out var t); _spent[what] = t + ms;
            _calls.TryGetValue(what, out var c); _calls[what] = c + 1;
        }

        // Called once per frame (from DogShareBehaviour.Update) and before recording, so the totals are per frame.
        internal static void Roll()
        {
            int f;
            try { f = UnityEngine.Time.frameCount; } catch { return; }
            if (f == _frame) return;
            double now = _sw.Elapsed.TotalMilliseconds;
            if (_frame >= 0 && _last >= 0)
            {
                double frameMs = now - _last;
                if (frameMs >= HitchMs) Report(frameMs);
            }
            _frame = f; _last = now;
            _spent.Clear(); _calls.Clear();
        }

        static void Report(double frameMs)
        {
            _hitches++;
            double ours = 0; var parts = new List<string>();
            foreach (var kv in _spent)
            {
                ours += kv.Value;
                parts.Add($"{kv.Key} {kv.Value:0.0} ms x{_calls[kv.Key]}");
            }
            string gc = "";
            try { gc = $", .NET GC so far: gen0 {GC.CollectionCount(0)} gen1 {GC.CollectionCount(1)} gen2 {GC.CollectionCount(2)}"; } catch { }
            string msg = $"Freeze #{_hitches}: a frame took {frameMs:0} ms; DogShare spent {ours:0.0} ms of it" +
                         (parts.Count > 0 ? " [" + string.Join(", ", parts) + "]" : "") + gc;
            if (_hitches <= 50 || _hitches % 20 == 0) DLog.Info("perf", msg);
        }
    }
}
