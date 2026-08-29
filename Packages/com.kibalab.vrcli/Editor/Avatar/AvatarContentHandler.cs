using System.Threading.Tasks;

namespace KibaLab.WorldDeployment.Editor
{
    internal sealed class AvatarContentHandler : IContentHandler
    {
        public ContentType ContentType => KibaLab.WorldDeployment.Editor.ContentType.Avatar;

        public Task<CheckReport> CheckAsync(DeploymentRequest request, string scenePath) =>
            AvatarChecker.RunAsync(request, scenePath);

        public Task<DeploymentOutcome> DeployAsync(DeploymentRequest request, string scenePath) =>
            AvatarDeployer.DeployAsync(request, scenePath);
    }
}
