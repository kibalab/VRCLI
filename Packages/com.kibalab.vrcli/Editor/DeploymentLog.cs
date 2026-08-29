using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace KibaLab.WorldDeployment.Editor
{
    internal static class DeploymentLog
    {
        private static readonly Stopwatch Clock = new Stopwatch();
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, long> PhaseMilliseconds = new Dictionary<string, long>();
        private static readonly List<string> PhaseOrder = new List<string>();
        private static string version;
        private static long phaseStartedAt;
        private static bool phaseStarted;

        public static string Version
        {
            get
            {
                if (version != null) return version;
                UnityEditor.PackageManager.PackageInfo package =
                    UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(DeploymentLog).Assembly);
                version = package != null ? package.version : "unknown";
                return version;
            }
        }

        public static string CurrentPhase { get; private set; } = "BOOT";

        public static void Start()
        {
            lock (Gate)
            {
                Clock.Restart();
                CurrentPhase = "BOOT";
                PhaseMilliseconds.Clear();
                PhaseOrder.Clear();
                phaseStartedAt = 0;
                phaseStarted = false;
            }
        }

        public static void Phase(string phase, string message)
        {
            lock (Gate)
            {
                CompleteCurrentPhase(Clock.ElapsedMilliseconds);
                CurrentPhase = phase;
                phaseStartedAt = Clock.ElapsedMilliseconds;
                phaseStarted = true;
                if (!PhaseOrder.Contains(phase)) PhaseOrder.Add(phase);
            }
            Info(phase, "▶ " + message);
        }

        public static long ElapsedMilliseconds => Clock.ElapsedMilliseconds;

        public static PhaseTiming[] SnapshotTimings()
        {
            lock (Gate)
            {
                long now = Clock.ElapsedMilliseconds;
                Dictionary<string, long> snapshot = new Dictionary<string, long>(PhaseMilliseconds);
                if (phaseStarted)
                {
                    long current = now - phaseStartedAt;
                    snapshot[CurrentPhase] = snapshot.TryGetValue(CurrentPhase, out long existing)
                        ? existing + current
                        : current;
                }
                return PhaseOrder
                    .Select(phase => new PhaseTiming { Phase = phase, DurationMs = snapshot.TryGetValue(phase, out long duration) ? duration : 0 })
                    .ToArray();
            }
        }

        private static void CompleteCurrentPhase(long now)
        {
            if (!phaseStarted) return;
            long duration = now - phaseStartedAt;
            PhaseMilliseconds[CurrentPhase] = PhaseMilliseconds.TryGetValue(CurrentPhase, out long existing)
                ? existing + duration
                : duration;
        }

        public static void Info(string area, string message)
        {
            string elapsed = Clock.Elapsed.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
            UnityEngine.Debug.Log("[VRCLI][" + elapsed + "][" + area + "] " + message);
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024L * 1024L) return (bytes / 1024d).ToString("F1", CultureInfo.InvariantCulture) + " KiB";
            if (bytes < 1024L * 1024L * 1024L) return (bytes / (1024d * 1024d)).ToString("F1", CultureInfo.InvariantCulture) + " MiB";
            return (bytes / (1024d * 1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture) + " GiB";
        }
    }

    [Serializable]
    internal sealed class PhaseTiming
    {
        public string Phase;
        public long DurationMs;
    }
}
