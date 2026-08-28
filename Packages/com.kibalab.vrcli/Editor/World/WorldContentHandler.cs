using System.Threading.Tasks;

namespace KibaLab.WorldDeployment.Editor
{
    internal sealed class WorldContentHandler : IContentHandler
    {
        public ContentType ContentType => KibaLab.WorldDeployment.Editor.ContentType.World;

        public Task<CheckReport> CheckAsync(DeploymentRequest request, string scenePath) =>
            WorldChecker.RunAsync(request, scenePath);

        public async Task<DeploymentOutcome> DeployAsync(DeploymentRequest request, string scenePath)
        {
            string blueprint = await WorldDeployer.DeployAsync(request, scenePath);
            return new DeploymentOutcome
            {
                Blueprint = blueprint,
                Created = request.IsNew,
                Message = request.IsNew ? "World created, built, and uploaded." : "World build and upload completed."
            };
        }
    }
}
