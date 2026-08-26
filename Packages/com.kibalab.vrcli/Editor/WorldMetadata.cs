using System;
using System.Collections.Generic;
using System.Globalization;
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

        public static async Task<string> UpdateAsync(DeploymentRequest request)
        {
            DeploymentLog.Phase("WORLD", "Fetching the existing VRChat world record.");
            VRCWorld world = await FetchOwnedAsync(request.BlueprintId);
            DeploymentLog.Info("WORLD", "World: " + world.Name + " (version " + world.Version + ")");
            DeploymentLog.Info("WORLD", "Ownership confirmed for the authenticated account.");
            Apply(ref world, request);

            VRCWorld updated = world;
            if (request.HasMetadataUpdate)
            {
                DeploymentLog.Phase("UPLOAD", "Saving requested world metadata fields without building a bundle.");
                updated = await VRCApi.UpdateWorldInfo(request.BlueprintId, world);
                DeploymentLog.Info("UPLOAD", "World metadata fields were updated.");
            }
            if (request.UpdateThumbnail)
            {
                DeploymentLog.Phase("UPLOAD", "Uploading the replacement world thumbnail without building a bundle.");
                updated = await VRCApi.UpdateWorldImage(
                    request.BlueprintId,
                    updated,
                    request.ThumbnailPath,
                    (status, progress) => DeploymentLog.Info(
                        "UPLOAD",
                        (string.IsNullOrWhiteSpace(status) ? "thumbnail" : status) + " " +
                        (Math.Max(0f, Math.Min(1f, progress)) * 100f).ToString("F1", CultureInfo.InvariantCulture) + "%"));
                DeploymentLog.Info("UPLOAD", "World thumbnail was updated.");
            }

            DeploymentLog.Info("UPLOAD", "Server world version: " + updated.Version);
            DeploymentLog.Info("UPLOAD", "Server updated time: " + updated.UpdatedAt.ToUniversalTime().ToString("O"));
            return updated.ID;
        }
    }
}
