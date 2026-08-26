using System;
using System.IO;
using UnityEngine;

namespace KibaLab.VRCLI.Editor
{
    [Serializable]
    internal sealed class VrcliResult
    {
        public bool Success;
        public int ExitCode;
        public string WorldId;
        public bool Created;
        public string Platform;
        public string Stage;
        public string Message;

        public static void Write(string resultFile, bool success, int exitCode, string worldId, bool created, string platform, string stage, string message)
        {
            VrcliResult result = new VrcliResult
            {
                Success = success,
                ExitCode = exitCode,
                WorldId = worldId,
                Created = created,
                Platform = platform,
                Stage = stage,
                Message = message
            };

            string directory = Path.GetDirectoryName(resultFile);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(resultFile, JsonUtility.ToJson(result, true));
        }
    }
}
