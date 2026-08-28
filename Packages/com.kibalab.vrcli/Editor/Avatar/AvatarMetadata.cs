using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VRC.Core;
using VRC.SDKBase.Editor.Api;

namespace KibaLab.WorldDeployment.Editor
{
    internal static class AvatarMetadata
    {
        public static async Task<VRCAvatar> FetchOwnedAsync(string blueprintId)
        {
            VRCAvatar avatar = await VRCApi.GetAvatar(blueprintId, true);
            if (string.IsNullOrWhiteSpace(avatar.ID))
                throw new InvalidOperationException("VRChat did not return avatar data for " + blueprintId + ".");
            if (APIUser.CurrentUser == null || avatar.AuthorId != APIUser.CurrentUser.id)
                throw new ContentOwnershipException("The logged-in account does not own " + blueprintId + ".");
            return avatar;
        }

        public static VRCAvatar Create(DeploymentRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Title))
                throw new ArgumentException("A new avatar requires --title <name>.");
            if (string.IsNullOrWhiteSpace(request.ThumbnailPath) || !File.Exists(request.ThumbnailPath))
                throw new FileNotFoundException("A new avatar requires an existing --thumbnail image.", request.ThumbnailPath);
            return new VRCAvatar
            {
                Name = request.Title,
                Description = request.Description ?? string.Empty,
                Tags = new List<string>(request.Tags ?? new string[0]),
                ReleaseStatus = "private"
            };
        }

        public static void Apply(ref VRCAvatar avatar, DeploymentRequest request)
        {
            if (request.UpdateTitle)
            {
                avatar.Name = request.Title;
                DeploymentLog.Info("AVATAR", "Requested name update: " + avatar.Name);
            }
            if (request.UpdateDescription)
            {
                avatar.Description = request.Description ?? string.Empty;
                DeploymentLog.Info("AVATAR", "Requested description update (" + avatar.Description.Length + " characters).");
            }
            if (request.UpdateTags)
            {
                List<string> tags = avatar.Tags ?? new List<string>();
                foreach (string tag in request.Tags)
                    if (!tags.Contains(tag)) tags.Add(tag);
                avatar.Tags = tags;
                DeploymentLog.Info("AVATAR", "Requested tags merged: " +
                    (request.Tags.Length == 0 ? "no additions" : string.Join(", ", request.Tags)));
            }
            if (request.UpdateThumbnail && !File.Exists(request.ThumbnailPath))
                throw new FileNotFoundException("Replacement thumbnail was not found.", request.ThumbnailPath);
        }
    }
}
