// AppBusy.cs
//
// One place that answers "is something running right now, and who owns it?".
//
// Why this exists: the Auto Package chain does not run in isolation, it DRIVES the
// manual pages (RunPsadtCommandAndThenAutoPackage navigates to ExecutePackagePage and
// PublishPackagePage and reuses their state). So an auto run and a manual click are not
// two windows open side by side, they are two owners of the same page. Letting both
// happen at once corrupts whichever one loses the race.
//
// The rule enforced here:
//   * while an AUTO run is live, the manual feature buttons are disabled
//   * while a MANUAL feature is working, Auto Package is disabled and the scheduler
//     holds off (WITHOUT spending the schedule slot, so the download is delayed, never
//     lost - see the retry contract in SourceDownloadPage)
//
// Only the NAV BUTTONS are disabled, never the pages themselves: the auto chain
// navigates programmatically and would otherwise break its own flow.
//
// Ownership is keyed by feature name rather than counted, so the same feature reporting
// "busy" twice cannot leave a stuck lock, and the reason can be shown in a tooltip.
// Features do not nest, so a plain add/remove is the right primitive.

using System;
using System.Collections.Generic;
using System.Linq;

namespace SwiftPack.Views
{
    internal static class AppBusy
    {
        private static readonly object _gate = new object();
        private static readonly HashSet<string> _auto   = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _manual = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised (on the caller's thread) whenever ownership changes.</summary>
        public static event Action? Changed;

        public static bool AutoRunning   { get { lock (_gate) return _auto.Count   > 0; } }
        public static bool ManualRunning { get { lock (_gate) return _manual.Count > 0; } }

        /// <summary>What is running, for tooltips and log lines. Empty when idle.</summary>
        public static string AutoReason   { get { lock (_gate) return string.Join(", ", _auto);   } }
        public static string ManualReason { get { lock (_gate) return string.Join(", ", _manual); } }

        /// <summary>
        /// Mark an auto operation (download, package chain) as running or finished.
        /// Idempotent: setting the same key twice is one owner, clearing an unknown key
        /// is a no-op, so a missed clear cannot double-lock and a stray clear cannot
        /// unlock someone else's work.
        /// </summary>
        public static void SetAuto(string key, bool running) => Set(_auto, key, running);

        /// <summary>Mark a manual feature (Create Package, Execute Package, ...) as running or finished.</summary>
        public static void SetManual(string key, bool running) => Set(_manual, key, running);

        private static void Set(HashSet<string> set, string key, bool running)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            bool changed;
            lock (_gate) changed = running ? set.Add(key) : set.Remove(key);
            if (changed) Changed?.Invoke();
        }

        /// <summary>
        /// Scope form for code with a clear start and end:
        ///     using (AppBusy.Manual("Create Intunewin")) { ... }
        /// The finally-equivalent means an exception cannot strand the lock.
        /// </summary>
        public static IDisposable Manual(string key) => new Scope(_manual, key);
        public static IDisposable Auto(string key)   => new Scope(_auto,   key);

        private sealed class Scope : IDisposable
        {
            private readonly HashSet<string> _set;
            private readonly string _key;
            private bool _done;
            public Scope(HashSet<string> set, string key) { _set = set; _key = key; Set(set, key, true); }
            public void Dispose()
            {
                if (_done) return;
                _done = true;
                Set(_set, _key, false);
            }
        }

        /// <summary>
        /// Release everything. For the app-level watchdog only: if a run ever wedges, the
        /// nav must not stay locked forever.
        /// </summary>
        public static void ResetAll()
        {
            bool changed;
            lock (_gate)
            {
                changed = _auto.Count > 0 || _manual.Count > 0;
                _auto.Clear();
                _manual.Clear();
            }
            if (changed) Changed?.Invoke();
        }
    }
}
