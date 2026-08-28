using System;
using System.Linq;
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

        public static AvatarTarget Find(string requestedBlueprint)
        {
            VRC_AvatarDescriptor[] descriptors = Resources.FindObjectsOfTypeAll<VRC_AvatarDescriptor>()
                .Where(descriptor => descriptor != null && descriptor.gameObject.scene.IsValid() && descriptor.gameObject.scene.isLoaded)
                .ToArray();
            if (descriptors.Length == 0)
                throw new InvalidOperationException("No VRC_AvatarDescriptor was found in the selected scene.");

            VRC_AvatarDescriptor selected = null;
            if (!string.IsNullOrWhiteSpace(requestedBlueprint))
            {
                selected = descriptors.FirstOrDefault(descriptor =>
                {
                    PipelineManager pipeline = descriptor.GetComponent<PipelineManager>();
                    return pipeline != null && string.Equals(pipeline.blueprintId, requestedBlueprint, StringComparison.Ordinal);
                });
                if (selected == null && descriptors.Length == 1) selected = descriptors[0];
                if (selected == null)
                    throw new InvalidOperationException("No avatar in the selected scene matches Blueprint " + requestedBlueprint + ".");
            }
            else if (descriptors.Length == 1)
            {
                selected = descriptors[0];
            }
            else
            {
                throw new InvalidOperationException(
                    "The selected scene contains " + descriptors.Length +
                    " avatars. Use --blueprint <avtr_id> to select an existing avatar, or keep one upload target in the scene.");
            }

            PipelineManager selectedPipeline = selected.GetComponent<PipelineManager>();
            if (selectedPipeline == null)
                throw new InvalidOperationException("The selected avatar has no PipelineManager component.");

            if (!string.IsNullOrWhiteSpace(requestedBlueprint)) selectedPipeline.blueprintId = requestedBlueprint;
            return new AvatarTarget { GameObject = selected.gameObject, Pipeline = selectedPipeline };
        }
    }
}
