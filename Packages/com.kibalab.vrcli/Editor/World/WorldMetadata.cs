using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VRC;
using VRC.Core;
using VRC.SDKBase.Editor.Api;

namespace KibaLab.WorldDeployment.Editor
{
    internal static class WorldMetadata
    {
        public static async Task<VRCWorld> FetchOwnedAsync(string blueprintId)
        {
            VRCWorld world = await VRCApi.GetWorld(blueprintId, true);
            if (string.IsNullOrWhiteSpace(world.ID))
                throw new InvalidOperationException("VRChat did not return world data for " + blueprintId + ".");
            if (APIUser.CurrentUser == null || world.AuthorId != APIUser.CurrentUser.id)
                throw new ContentOwnershipException("The logged-in account does not own " + blueprintId + ".");
            return world;
        }

        public static void Apply(ref VRCWorld world, DeploymentRequest request)
        {
            if (request.UpdateThumbnail && !File.Exists(request.ThumbnailPath))
                throw new FileNotFoundException("Replacement thumbnail was not found.", request.ThumbnailPath);

            if (request.UpdateTitle)
            {
                world.Name = request.Title;
                DeploymentLog.Info("WORLD", "Requested name update: " + world.Name);
            }
            if (request.UpdateDescription)
            {
                world.Description = request.Description ?? string.Empty;
                DeploymentLog.Info("WORLD", "Requested description update (" + world.Description.Length + " characters).");
            }
            if (request.UpdateCapacity)
            {
                world.Capacity = request.Capacity;
                DeploymentLog.Info("WORLD", "Requested maximum capacity update: " + world.Capacity);
            }
            if (request.UpdateRecommendedCapacity)
            {
                if (request.RecommendedCapacity > world.Capacity)
                {
                    throw new ArgumentException(
                        "Recommended capacity " + request.RecommendedCapacity +
                        " exceeds the effective maximum capacity " + world.Capacity + ".");
                }
                world.RecommendedCapacity = request.RecommendedCapacity;
                DeploymentLog.Info("WORLD", "Requested recommended capacity update: " + world.RecommendedCapacity);
            }
            else if (request.UpdateCapacity && world.RecommendedCapacity > world.Capacity)
            {
                world.RecommendedCapacity = world.Capacity;
                DeploymentLog.Info("WORLD", "Recommended capacity was clamped to the new maximum: " + world.RecommendedCapacity);
            }
            if (request.UpdateTags)
            {
                List<string> tags = world.Tags ?? new List<string>();
                foreach (string tag in request.Tags)
                {
                    if (!tags.Contains(tag)) tags.Add(tag);
                }
                world.Tags = tags;
                DeploymentLog.Info("WORLD", "Requested tags merged: " +
                    (request.Tags.Length == 0 ? "no additions" : string.Join(", ", request.Tags)));
            }
            if (request.UpdateThumbnail)
                DeploymentLog.Info("WORLD", "Requested thumbnail replacement: " + request.ThumbnailPath);
        }

    }
}
