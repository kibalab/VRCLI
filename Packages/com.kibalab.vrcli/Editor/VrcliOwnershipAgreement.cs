using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using VRC.SDKBase.Editor.Api;

namespace KibaLab.VRCLI.Editor
{
    internal static class VrcliOwnershipAgreement
    {
        private const int Version = 1;
        private const string AgreementCode = "content.copyright.owned";
        private const string SessionKey = "VRCSdkControlPanel.CopyrightAgreement.ContentList";

        public static async Task AcceptForNewContentAsync(string contentId, bool accepted)
        {
            VrcliLog.Phase("OWNERSHIP", "Recording content ownership consent for the new world.");
            if (!accepted)
            {
                throw new VrcliOwnershipException("Creating a world requires --yes to certify that you have the rights to upload its content.");
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
                throw new VrcliOwnershipException("VRChat rejected the content ownership consent for the new world.");
            }
            MarkSessionAccepted(contentId);
            VrcliLog.Info("OWNERSHIP", "Content ownership consent version " + Version + " was accepted.");
        }

        public static async Task EnsureAsync(string contentId, bool acceptWhenMissing)
        {
            VrcliLog.Phase("OWNERSHIP", "Checking content ownership consent.");
            VRCAgreementCheckResponse check = await VRCApi.CheckContentUploadConsent(new VRCAgreementCheckRequest
            {
                AgreementCode = AgreementCode,
                ContentId = contentId,
                Version = Version
            });

            if (!check.Agreed)
            {
                if (!acceptWhenMissing)
                {
                    throw new VrcliOwnershipException("Content ownership consent is missing. Re-run with --yes to certify that you have the rights to upload this world.");
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
                    throw new VrcliOwnershipException("VRChat rejected the content ownership consent.");
                }
                VrcliLog.Info("OWNERSHIP", "Missing consent was accepted and recorded.");
            }
            else
            {
                VrcliLog.Info("OWNERSHIP", "Existing content ownership consent is valid.");
            }

            MarkSessionAccepted(contentId);
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
