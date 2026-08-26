using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using VRC.SDKBase.Editor.Api;

namespace KibaLab.WorldDeployment.Editor
{
    internal static class OwnershipAgreement
    {
        private const int Version = 1;
        private const string AgreementCode = "content.copyright.owned";
        private const string SessionKey = "VRCSdkControlPanel.CopyrightAgreement.ContentList";

        public static async Task AcceptForNewContentAsync(string contentId, bool accepted)
        {
            DeploymentLog.Phase("OWNERSHIP", "Recording content ownership consent for the new world.");
            if (!accepted)
            {
                throw new ContentOwnershipException("Creating a world requires --yes to certify that you have the rights to upload its content.");
            }

            // A provisional new-world ID has no world record yet, so checking first
            // returns 404. The agreement endpoint accepts the ID independently; post
            // it directly so the SDK's mandatory pre-upload check can succeed.
            VRCAgreement result = await VRCApi.ContentUploadConsent(new VRCAgreement
            {
                AgreementCode = AgreementCode,
                AgreementFulltext = VRC.SDKBase.VRCCopyrightAgreement.AgreementText,
                ContentId = contentId,
                Version = Version
            });
            if (result.ContentId != contentId || result.AgreementCode != AgreementCode || result.Version != Version)
            {
                throw new ContentOwnershipException("VRChat rejected the content ownership consent for the new world.");
            }
            MarkSessionAccepted(contentId);
            DeploymentLog.Info("OWNERSHIP", "Content ownership consent version " + Version + " was accepted.");
        }

        public static async Task EnsureAsync(string contentId, bool acceptWhenMissing)
        {
            DeploymentLog.Phase("OWNERSHIP", "Checking content ownership consent.");
            bool agreed = await CheckAsync(contentId);

            if (!agreed)
            {
                if (!acceptWhenMissing)
                {
                    throw new ContentOwnershipException("Content ownership consent is missing. Re-run with --yes to certify that you have the rights to upload this world.");
                }

                VRCAgreement result = await VRCApi.ContentUploadConsent(new VRCAgreement
                {
                    AgreementCode = AgreementCode,
                    AgreementFulltext = VRC.SDKBase.VRCCopyrightAgreement.AgreementText,
                    ContentId = contentId,
                    Version = Version
                });

                if (result.ContentId != contentId || result.AgreementCode != AgreementCode || result.Version != Version)
                {
                    throw new ContentOwnershipException("VRChat rejected the content ownership consent.");
                }
                DeploymentLog.Info("OWNERSHIP", "Missing consent was accepted and recorded.");
            }
            else
            {
                DeploymentLog.Info("OWNERSHIP", "Existing content ownership consent is valid.");
            }

            MarkSessionAccepted(contentId);
        }

        public static async Task<bool> CheckAsync(string contentId)
        {
            VRCAgreementCheckResponse check = await VRCApi.CheckContentUploadConsent(new VRCAgreementCheckRequest
            {
                AgreementCode = AgreementCode,
                ContentId = contentId,
                Version = Version
            });
            return check.Agreed;
        }

        private static void MarkSessionAccepted(string contentId)
        {
            HashSet<string> ids = new HashSet<string>(
                (SessionState.GetString(SessionKey, string.Empty) ?? string.Empty)
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);
            ids.Add(contentId);
            SessionState.SetString(SessionKey, string.Join(";", ids.ToArray()));
        }
    }
}
