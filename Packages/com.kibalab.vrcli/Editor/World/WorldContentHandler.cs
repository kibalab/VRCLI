using System.Threading.Tasks;

namespace KibaLab.WorldDeployment.Editor
{
    internal sealed class WorldContentHandler : IContentHandler
    {
        public ContentType ContentType => KibaLab.WorldDeployment.Editor.ContentType.World;

        public Task<CheckReport> CheckAsync(DeploymentRequest request, string scenePath) =>
            WorldChecker.RunAsync(request, scenePath);

        public Task<DeploymentOutcome> DeployAsync(DeploymentRequest request, string scenePath) =>
            WorldDeployer.DeployAsync(request, scenePath);
    }
}
