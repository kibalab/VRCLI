namespace KibaLab.WorldDeployment
{
    public sealed class RecoveryManifest
    {
        public int FormatVersion = 1;
        public string ProjectPath = string.Empty;
        public string ContentType = string.Empty;
        public string Blueprint = string.Empty;
        public bool IsNew;
        public string Title = string.Empty;
        public string Description = string.Empty;
        public string ThumbnailPath = string.Empty;
        public int Capacity;
        public int RecommendedCapacity;
        public string[] Tags = new string[0];
        public bool UpdateTitle;
        public bool UpdateDescription;
        public bool UpdateThumbnail;
        public bool UpdateCapacity;
        public bool UpdateRecommendedCapacity;
        public bool UpdateTags;
        public string Platform = string.Empty;
        public string ScenePath = string.Empty;
        public string TargetPath = string.Empty;
        public string BundlePath = string.Empty;
        public string Signature = string.Empty;
    }
}
