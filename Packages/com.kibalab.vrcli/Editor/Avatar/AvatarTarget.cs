using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace KibaLab.WorldDeployment.Editor
{
    internal sealed class AvatarTarget
    {
        public GameObject GameObject { get; private set; }
        public PipelineManager Pipeline { get; private set; }

        public string Selector { get; private set; }
        private GameObject[] activatedObjects = new GameObject[0];

        public static async Task<AvatarTarget> FindAsync(DeploymentRequest request)
        {
            VRC_AvatarDescriptor[] descriptors = Resources.FindObjectsOfTypeAll<VRC_AvatarDescriptor>()
                .Where(descriptor => descriptor != null && descriptor.gameObject.scene.IsValid() && descriptor.gameObject.scene.isLoaded)
                .OrderBy(descriptor => HierarchyPath(descriptor.transform), StringComparer.Ordinal)
                .ToArray();
            if (descriptors.Length == 0)
                throw new InvalidOperationException("No VRC_AvatarDescriptor was found in the selected scene.");

            ContentTarget[] candidates = descriptors.Select(ToCandidate).ToArray();
            EnsureUniqueSelectors(candidates);
            VRC_AvatarDescriptor targetMatch = FindByTarget(descriptors, request.TargetPath);
            if (!string.IsNullOrWhiteSpace(request.TargetPath) && targetMatch == null)
                throw new TargetSelectionException("No avatar matches --target '" + request.TargetPath + "'.", candidates);
            VRC_AvatarDescriptor blueprintMatch = FindByBlueprint(descriptors, request.BlueprintId, targetMatch, candidates);

            VRC_AvatarDescriptor selected;
            if (targetMatch != null && !string.IsNullOrWhiteSpace(request.BlueprintId))
            {
                PipelineManager targetPipeline = RequirePipeline(targetMatch);
                if (blueprintMatch != null && blueprintMatch != targetMatch)
                    throw new ArgumentException("--target and --blueprint identify different avatars in the selected scene.");
                if (!string.IsNullOrWhiteSpace(targetPipeline.blueprintId) &&
                    !string.Equals(targetPipeline.blueprintId, request.BlueprintId, StringComparison.Ordinal))
                    throw new ArgumentException("The selected --target already has a different Blueprint: " + targetPipeline.blueprintId + ".");
                selected = targetMatch;
            }
            else if (targetMatch != null)
            {
                selected = targetMatch;
            }
            else if (blueprintMatch != null)
            {
                selected = blueprintMatch;
            }
            else if (!string.IsNullOrWhiteSpace(request.BlueprintId) && descriptors.Length == 1)
            {
                selected = descriptors[0];
            }
            else if (!string.IsNullOrWhiteSpace(request.BlueprintId))
            {
                throw new TargetSelectionException(
                    "No scene avatar has Blueprint " + request.BlueprintId + ". Add --target to bind that Blueprint to a specific avatar.",
                    candidates);
            }
            else if (descriptors.Length == 1)
            {
                selected = descriptors[0];
            }
            else
            {
                selected = await RequestSelectionAsync(request, descriptors, candidates);
            }

            PipelineManager selectedPipeline = RequirePipeline(selected);
            if (!string.IsNullOrWhiteSpace(request.BlueprintId)) selectedPipeline.blueprintId = request.BlueprintId;
            string selector = HierarchyPath(selected.transform);
            GameObject[] activatedObjects = ActivateHierarchy(selected.transform);
            DeploymentLog.Phase("TARGET", "Selected avatar: " + selector +
                (string.IsNullOrWhiteSpace(selectedPipeline.blueprintId) ? " (new avatar)" : " (" + selectedPipeline.blueprintId + ")"));
            return new AvatarTarget
            {
                GameObject = selected.gameObject,
                Pipeline = selectedPipeline,
                Selector = selector,
                activatedObjects = activatedObjects
            };
        }

        public void RestoreActivation()
        {
            foreach (GameObject activatedObject in activatedObjects)
            {
                if (activatedObject != null) activatedObject.SetActive(false);
            }
            activatedObjects = new GameObject[0];
        }

        private static async Task<VRC_AvatarDescriptor> RequestSelectionAsync(
            DeploymentRequest request,
            VRC_AvatarDescriptor[] descriptors,
            ContentTarget[] candidates)
        {
            const string message = "Multiple avatars were found. Provide --target or --blueprint.";
            if (string.IsNullOrWhiteSpace(request.TargetRequestFile) ||
                string.IsNullOrWhiteSpace(request.TargetResponseFile))
                throw new TargetSelectionException(message, candidates);

            string directory = Path.GetDirectoryName(request.TargetRequestFile);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(
                request.TargetRequestFile,
                JsonUtility.ToJson(new TargetSelectionRequest { Targets = candidates }, true));
            DeploymentLog.Phase("TARGET", "Multiple avatars found; waiting for target selection.");

            DateTime deadline = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(request.TargetResponseFile))
                {
                    string selector = File.ReadAllText(request.TargetResponseFile).Trim();
                    if (string.Equals(selector, "__VRCLI_CANCELLED__", StringComparison.Ordinal))
                        throw new OperationCanceledException("Avatar target selection was cancelled.");
                    VRC_AvatarDescriptor selected = FindByTarget(descriptors, selector);
                    if (selected == null)
                        throw new ArgumentException("The selected avatar target is no longer available: " + selector);
                    return selected;
                }
                await Task.Delay(100);
            }
            throw new TimeoutException("Timed out while waiting for avatar target selection.");
        }

        private static VRC_AvatarDescriptor FindByTarget(VRC_AvatarDescriptor[] descriptors, string selector)
        {
            if (string.IsNullOrWhiteSpace(selector)) return null;
            return descriptors.FirstOrDefault(descriptor =>
                string.Equals(HierarchyPath(descriptor.transform), selector, StringComparison.Ordinal));
        }

        private static VRC_AvatarDescriptor FindByBlueprint(
            VRC_AvatarDescriptor[] descriptors,
            string blueprint,
            VRC_AvatarDescriptor targetMatch,
            ContentTarget[] candidates)
        {
            if (string.IsNullOrWhiteSpace(blueprint)) return null;
            VRC_AvatarDescriptor[] matches = descriptors.Where(descriptor =>
                    string.Equals(RequirePipeline(descriptor).blueprintId, blueprint, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length > 1 && targetMatch != null && matches.Contains(targetMatch))
                return targetMatch;
            if (matches.Length > 1)
                throw new TargetSelectionException(
                    "Blueprint " + blueprint + " is assigned to multiple scene avatars. Use --target to choose one.",
                    candidates);
            return matches.FirstOrDefault();
        }

        private static PipelineManager RequirePipeline(VRC_AvatarDescriptor descriptor)
        {
            PipelineManager pipeline = descriptor.GetComponent<PipelineManager>();
            if (pipeline == null)
                throw new InvalidOperationException("Avatar '" + HierarchyPath(descriptor.transform) + "' has no PipelineManager component.");
            return pipeline;
        }

        private static ContentTarget ToCandidate(VRC_AvatarDescriptor descriptor)
        {
            PipelineManager pipeline = RequirePipeline(descriptor);
            return new ContentTarget
            {
                Name = descriptor.gameObject.name,
                Selector = HierarchyPath(descriptor.transform),
                Blueprint = string.IsNullOrWhiteSpace(pipeline.blueprintId) ? null : pipeline.blueprintId
            };
        }

        private static string HierarchyPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }
            return path;
        }

        private static GameObject[] ActivateHierarchy(Transform transform)
        {
            List<GameObject> activated = new List<GameObject>();
            while (transform != null)
            {
                if (!transform.gameObject.activeSelf)
                {
                    transform.gameObject.SetActive(true);
                    activated.Add(transform.gameObject);
                }
                transform = transform.parent;
            }
            return activated.ToArray();
        }

        private static void EnsureUniqueSelectors(ContentTarget[] candidates)
        {
            string duplicate = candidates.GroupBy(candidate => candidate.Selector, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1)?.Key;
            if (duplicate != null)
                throw new InvalidOperationException(
                    "Multiple avatars have the same Hierarchy path '" + duplicate + "'. Rename duplicate GameObjects before deployment.");
        }
    }
}
