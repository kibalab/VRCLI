using System;
using System.Globalization;
using System.Linq;

namespace KibaLab.VRCLI.Editor
{
    internal sealed class VrcliRequest
    {
        public string BlueprintId { get; private set; }
        public bool CreateWorld { get; private set; }
        public string WorldName { get; private set; }
        public string WorldDescription { get; private set; }
        public string ThumbnailPath { get; private set; }
        public int Capacity { get; private set; }
        public int RecommendedCapacity { get; private set; }
        public string[] Tags { get; private set; }
        public string Username { get; private set; }
        public string Password { get; private set; }
        public string TwoFactorCode { get; private set; }
        public string TotpSecret { get; private set; }
        public string Platform { get; private set; }
        public string ScenePath { get; private set; }
        public string ResultFile { get; private set; }
        public bool AcceptContentOwnership { get; private set; }
        public bool UpdateWorldName { get; private set; }
        public bool UpdateWorldDescription { get; private set; }
        public bool UpdateThumbnail { get; private set; }
        public bool UpdateCapacity { get; private set; }
        public bool UpdateRecommendedCapacity { get; private set; }
        public bool UpdateWorldTags { get; private set; }

        public bool HasWorldInfoUpdate
        {
            get
            {
                return UpdateWorldName || UpdateWorldDescription || UpdateCapacity ||
                       UpdateRecommendedCapacity || UpdateWorldTags;
            }
        }

        public static VrcliRequest FromEnvironment()
        {
            VrcliRequest request = new VrcliRequest
            {
                BlueprintId = Require("VRCLI_BLUEPRINT_ID"),
                CreateWorld = ReadBoolean("VRCLI_CREATE_WORLD"),
                Username = Require("VRCLI_USERNAME"),
                Password = Require("VRCLI_PASSWORD"),
                TwoFactorCode = Environment.GetEnvironmentVariable("VRCLI_TWO_FACTOR_CODE"),
                TotpSecret = Environment.GetEnvironmentVariable("VRCLI_TOTP_SECRET"),
                Platform = Require("VRCLI_PLATFORM"),
                ResultFile = Require("VRCLI_RESULT_FILE"),
                ScenePath = Environment.GetEnvironmentVariable("VRCLI_SCENE"),
                AcceptContentOwnership = string.Equals(
                    Environment.GetEnvironmentVariable("VRCLI_ACCEPT_CONTENT_OWNERSHIP"),
                    "true",
                    StringComparison.OrdinalIgnoreCase)
            };

            if (request.CreateWorld)
            {
                request.WorldName = Require("VRCLI_WORLD_NAME");
                request.WorldDescription = Environment.GetEnvironmentVariable("VRCLI_WORLD_DESCRIPTION") ?? string.Empty;
                request.ThumbnailPath = Require("VRCLI_THUMBNAIL");
                request.Capacity = ReadInteger("VRCLI_CAPACITY");
                request.RecommendedCapacity = ReadInteger("VRCLI_RECOMMENDED_CAPACITY");
                request.Tags = (Environment.GetEnvironmentVariable("VRCLI_WORLD_TAGS") ?? string.Empty)
                    .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(tag => tag.Trim())
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                if (request.Capacity < 1)
                {
                    throw new ArgumentException("VRCLI_CAPACITY must be at least 1.");
                }
                if (request.RecommendedCapacity < 1 || request.RecommendedCapacity > request.Capacity)
                {
                    throw new ArgumentException("VRCLI_RECOMMENDED_CAPACITY must be from 1 to VRCLI_CAPACITY.");
                }
            }
            else
            {
                request.UpdateWorldName = ReadBoolean("VRCLI_UPDATE_WORLD_NAME");
                request.UpdateWorldDescription = ReadBoolean("VRCLI_UPDATE_WORLD_DESCRIPTION");
                request.UpdateThumbnail = ReadBoolean("VRCLI_UPDATE_THUMBNAIL");
                request.UpdateCapacity = ReadBoolean("VRCLI_UPDATE_CAPACITY");
                request.UpdateRecommendedCapacity = ReadBoolean("VRCLI_UPDATE_RECOMMENDED_CAPACITY");
                request.UpdateWorldTags = ReadBoolean("VRCLI_UPDATE_WORLD_TAGS");
                request.WorldName = Environment.GetEnvironmentVariable("VRCLI_WORLD_NAME");
                request.WorldDescription = Environment.GetEnvironmentVariable("VRCLI_WORLD_DESCRIPTION");
                request.ThumbnailPath = Environment.GetEnvironmentVariable("VRCLI_THUMBNAIL");
                request.Capacity = request.UpdateCapacity ? ReadInteger("VRCLI_CAPACITY") : 0;
                request.RecommendedCapacity = request.UpdateRecommendedCapacity
                    ? ReadInteger("VRCLI_RECOMMENDED_CAPACITY")
                    : 0;
                request.Tags = (Environment.GetEnvironmentVariable("VRCLI_WORLD_TAGS") ?? string.Empty)
                    .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(tag => tag.Trim())
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                if (request.UpdateWorldName && string.IsNullOrWhiteSpace(request.WorldName))
                    throw new ArgumentException("VRCLI_WORLD_NAME must not be empty when updating a world name.");
                if (request.UpdateThumbnail && string.IsNullOrWhiteSpace(request.ThumbnailPath))
                    throw new ArgumentException("VRCLI_THUMBNAIL is missing for the requested thumbnail update.");
                if (request.UpdateCapacity && request.Capacity < 1)
                    throw new ArgumentException("VRCLI_CAPACITY must be at least 1.");
                if (request.UpdateRecommendedCapacity && request.RecommendedCapacity < 1)
                    throw new ArgumentException("VRCLI_RECOMMENDED_CAPACITY must be at least 1.");
            }

            if (request.Platform != "StandaloneWindows64" && request.Platform != "Android")
            {
                throw new ArgumentException("VRCLI_PLATFORM must be StandaloneWindows64 or Android.");
            }

            return request;
        }

        private static bool ReadBoolean(string name)
        {
            return string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static int ReadInteger(string name)
        {
            int value;
            if (!int.TryParse(Require(name), NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                throw new ArgumentException(name + " must be an integer.");
            }
            return value;
        }

        private static string Require(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(name + " is missing.");
            }
            return value;
        }
    }
}
