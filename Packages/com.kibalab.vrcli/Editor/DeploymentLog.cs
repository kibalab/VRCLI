using System;
using System.Globalization;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace KibaLab.WorldDeployment.Editor
{
    internal static class DeploymentLog
    {
        private static readonly Stopwatch Clock = new Stopwatch();
        private static string version;

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
            Clock.Restart();
            CurrentPhase = "BOOT";
        }

        public static void Phase(string phase, string message)
        {
            CurrentPhase = phase;
            Info(phase, "▶ " + message);
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
}
