using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDKBase.Editor;

namespace KibaLab.WorldDeployment.Editor
{
    internal interface IContentHandler
    {
        ContentType ContentType { get; }
        Task<CheckReport> CheckAsync(DeploymentRequest request, string scenePath);
        Task<DeploymentOutcome> DeployAsync(DeploymentRequest request, string scenePath);
    }

    internal sealed class DeploymentOutcome
    {
        public string Blueprint { get; set; }
        public bool Created { get; set; }
        public string Message { get; set; }
        public int PreviousVersion { get; set; }
        public int ServerVersion { get; set; }
        public string ServerUpdatedAt { get; set; }
        public BuildArtifact Artifact { get; set; }
    }

    [Serializable]
    internal sealed class BuildArtifact
    {
        public string Path;
        public long Size;
        public string Sha256;
        public string RecoveryFile;

        public static BuildArtifact Capture(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha256 = SHA256.Create())
            {
                string hash = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
                return new BuildArtifact
                {
                    Path = path,
                    Size = stream.Length,
                    Sha256 = hash.ToLowerInvariant()
                };
            }
        }
    }

    [Serializable]
    internal sealed class ContentTarget
    {
        public string Name;
        public string Selector;
        public string Blueprint;
    }

    [Serializable]
    internal sealed class TargetSelectionRequest
    {
        public ContentTarget[] Targets;
    }

    internal sealed class TargetSelectionException : Exception
    {
        public ContentTarget[] Targets { get; private set; }

        public TargetSelectionException(string message, ContentTarget[] targets) : base(message)
        {
            Targets = targets;
        }
    }

    internal sealed class CheckReport
    {
        private readonly List<string> errors = new List<string>();
        private readonly List<string> warnings = new List<string>();
        private readonly List<string> information = new List<string>();

        public IReadOnlyList<string> Errors => errors;
        public IReadOnlyList<string> Warnings => warnings;
        public IReadOnlyList<string> Information => information;
        public string Blueprint { get; set; }
        public bool Success => errors.Count == 0;

        public void AddError(string message) => Add(errors, message);
        public void AddWarning(string message) => Add(warnings, message);
        public void AddInformation(string message) => Add(information, message);

        private static void Add(List<string> destination, string message)
        {
            if (string.IsNullOrWhiteSpace(message) || destination.Contains(message)) return;
            destination.Add(message);
        }
    }

    internal static class ContentScene
    {
        public static string Resolve(string scenePath)
        {
            string selected = scenePath;
            if (string.IsNullOrWhiteSpace(selected))
            {
                foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
                {
                    if (!scene.enabled) continue;
                    selected = scene.path;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(selected))
                throw new ArgumentException("No scene was specified and EditorBuildSettings has no enabled scene.");

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string fullPath = Path.IsPathRooted(selected)
                ? selected
                : Path.Combine(projectRoot, selected.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Scene was not found.", selected);
            return selected;
        }

        public static void Open(string scenePath)
        {
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            DeploymentLog.Info("PREPARE", "Opened content scene: " + scenePath);
        }

        public static void ValidatePlatform(string requestedPlatform)
        {
            BuildTarget expected = requestedPlatform == "Android" ? BuildTarget.Android : BuildTarget.StandaloneWindows64;
            if (EditorUserBuildSettings.activeBuildTarget != expected)
                throw new InvalidOperationException("Unity active build target is " + EditorUserBuildSettings.activeBuildTarget + ", but VRCLI requested " + expected + ".");
            DeploymentLog.Info("PREPARE", "Active Unity build target matches " + expected + ".");
            if (expected == BuildTarget.Android && EditorUserBuildSettings.androidBuildSubtarget != MobileTextureSubtarget.ASTC)
            {
                EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
                DeploymentLog.Info("PREPARE", "Android texture compression target changed to ASTC.");
            }
        }
    }

    internal static class SdkDiagnostics
    {
        public static void Collect(IVRCSdkBuilderApi builder, CheckReport report)
        {
            builder.CreateValidationsGUI(new VisualElement());
            CollectField(builder, "GUIErrors", report.AddError);
            CollectField(builder, "GUIWarnings", report.AddWarning);
            CollectField(builder, "GUIInfos", report.AddInformation);
            CollectField(builder, "GUILinks", report.AddInformation);
            CollectField(builder, "GUIStats", report.AddInformation);
        }

        private static void CollectField(IVRCSdkBuilderApi builder, string fieldName, Action<string> add)
        {
            FieldInfo panelField = FindField(builder.GetType(), "_builder");
            object panel = panelField != null ? panelField.GetValue(builder) : null;
            if (panel == null) throw new InvalidOperationException("The VRChat SDK validation panel is unavailable.");
            FieldInfo issuesField = FindField(panel.GetType(), fieldName);
            IDictionary issues = issuesField != null ? issuesField.GetValue(panel) as IDictionary : null;
            if (issues == null) throw new InvalidOperationException("The VRChat SDK " + fieldName + " report is unavailable.");
            foreach (DictionaryEntry entry in issues)
            {
                IEnumerable entries = entry.Value as IEnumerable;
                if (entries == null) continue;
                foreach (object issue in entries)
                {
                    FieldInfo textField = issue.GetType().GetField("issueText", BindingFlags.Instance | BindingFlags.Public);
                    string text = textField != null ? textField.GetValue(issue) as string : null;
                    if (!string.IsNullOrWhiteSpace(text)) add(text.Replace('\r', ' ').Replace('\n', ' ').Trim());
                }
            }
        }

        private static FieldInfo FindField(Type type, string name)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }
    }
}
