using System;
using System.Globalization;
using System.Linq;

namespace KibaLab.WorldDeployment.Editor
{
    internal sealed class DeploymentRequest
    {
        private static readonly char[] TagSeparators = { '|' };

        public RequestOperation Operation { get; private set; }
        public string BlueprintId { get; private set; }
        public bool IsNew { get; private set; }
        public string Title { get; private set; }
        public string Description { get; private set; }
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
        public bool OwnershipAccepted { get; private set; }
        public bool UpdateTitle { get; private set; }
        public bool UpdateDescription { get; private set; }
        public bool UpdateThumbnail { get; private set; }
        public bool UpdateCapacity { get; private set; }
        public bool UpdateRecommendedCapacity { get; private set; }
        public bool UpdateTags { get; private set; }

        public bool HasMetadataUpdate => UpdateTitle || UpdateDescription || UpdateCapacity ||
                                         UpdateRecommendedCapacity || UpdateTags;

        public static DeploymentRequest FromEnvironment()
        {
            DeploymentRequest request = new DeploymentRequest
            {
                Operation = ReadOperation(),
                BlueprintId = Environment.GetEnvironmentVariable(DeploymentEnvironment.BlueprintId) ?? string.Empty,
                IsNew = ReadBoolean(DeploymentEnvironment.IsNew),
                Username = Require(DeploymentEnvironment.Username),
                Password = Require(DeploymentEnvironment.Password),
                TwoFactorCode = Environment.GetEnvironmentVariable(DeploymentEnvironment.TwoFactorCode),
                TotpSecret = Environment.GetEnvironmentVariable(DeploymentEnvironment.TotpSecret),
                Platform = Require(DeploymentEnvironment.Platform),
                ResultFile = Require(DeploymentEnvironment.ResultFile),
                ScenePath = Environment.GetEnvironmentVariable(DeploymentEnvironment.Scene),
                OwnershipAccepted = string.Equals(
                    Environment.GetEnvironmentVariable(DeploymentEnvironment.OwnershipAccepted),
                    "true",
                    StringComparison.OrdinalIgnoreCase)
            };

            if (request.Operation != RequestOperation.Check && string.IsNullOrWhiteSpace(request.BlueprintId))
                throw new ArgumentException("VRCLI_BLUEPRINT_ID is missing.");
            if (request.Operation != RequestOperation.Deploy && request.IsNew)
                throw new ArgumentException("VRCLI_CREATE_WORLD is only valid for deployment.");

            if (request.IsNew)
            {
                request.Title = Require(DeploymentEnvironment.Title);
                request.Description = Environment.GetEnvironmentVariable(DeploymentEnvironment.Description) ?? string.Empty;
                request.ThumbnailPath = Require(DeploymentEnvironment.Thumbnail);
                request.Capacity = ReadInteger(DeploymentEnvironment.Capacity);
                request.RecommendedCapacity = ReadInteger(DeploymentEnvironment.RecommendedCapacity);
                request.Tags = ReadTags();

                EnsurePositive(DeploymentEnvironment.Capacity, request.Capacity);
                if (request.RecommendedCapacity < 1 || request.RecommendedCapacity > request.Capacity)
                {
                    throw new ArgumentException("VRCLI_RECOMMENDED_CAPACITY must be from 1 to VRCLI_CAPACITY.");
                }
            }
            else if (request.Operation != RequestOperation.Check)
            {
                request.UpdateTitle = ReadBoolean(DeploymentEnvironment.UpdateTitle);
                request.UpdateDescription = ReadBoolean(DeploymentEnvironment.UpdateDescription);
                request.UpdateThumbnail = ReadBoolean(DeploymentEnvironment.UpdateThumbnail);
                request.UpdateCapacity = ReadBoolean(DeploymentEnvironment.UpdateCapacity);
                request.UpdateRecommendedCapacity = ReadBoolean(DeploymentEnvironment.UpdateRecommendedCapacity);
                request.UpdateTags = ReadBoolean(DeploymentEnvironment.UpdateTags);
                request.Title = Environment.GetEnvironmentVariable(DeploymentEnvironment.Title);
                request.Description = Environment.GetEnvironmentVariable(DeploymentEnvironment.Description);
                request.ThumbnailPath = Environment.GetEnvironmentVariable(DeploymentEnvironment.Thumbnail);
                request.Capacity = request.UpdateCapacity ? ReadInteger(DeploymentEnvironment.Capacity) : 0;
                request.RecommendedCapacity = request.UpdateRecommendedCapacity
                    ? ReadInteger(DeploymentEnvironment.RecommendedCapacity)
                    : 0;
                request.Tags = ReadTags();

                if (request.UpdateTitle && string.IsNullOrWhiteSpace(request.Title))
                    throw new ArgumentException("VRCLI_WORLD_NAME must not be empty when updating a world name.");
                if (request.UpdateThumbnail && string.IsNullOrWhiteSpace(request.ThumbnailPath))
                    throw new ArgumentException("VRCLI_THUMBNAIL is missing for the requested thumbnail update.");
                if (request.UpdateCapacity) EnsurePositive(DeploymentEnvironment.Capacity, request.Capacity);
                if (request.UpdateRecommendedCapacity)
                    EnsurePositive(DeploymentEnvironment.RecommendedCapacity, request.RecommendedCapacity);
            }

            if (request.Platform != "StandaloneWindows64" && request.Platform != "Android")
            {
                throw new ArgumentException("VRCLI_PLATFORM must be StandaloneWindows64 or Android.");
            }

            return request;
        }

        private static RequestOperation ReadOperation()
        {
            string value = Require(DeploymentEnvironment.Operation);
            RequestOperation operation;
            if (!Enum.TryParse(value, true, out operation))
                throw new ArgumentException("VRCLI_OPERATION must be Deploy or Check.");
            return operation;
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

        private static string[] ReadTags()
        {
            return (Environment.GetEnvironmentVariable(DeploymentEnvironment.Tags) ?? string.Empty)
                .Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(tag => tag.Trim())
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static void EnsurePositive(string name, int value)
        {
            if (value < 1) throw new ArgumentException(name + " must be at least 1.");
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

    internal enum RequestOperation
    {
        Deploy,
        Check
    }
}
