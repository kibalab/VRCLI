using System;
using System.IO;
using UnityEngine;

namespace KibaLab.WorldDeployment.Editor
{
    [Serializable]
    internal sealed class DeploymentResult
    {
        public bool Success;
        public int ExitCode;
        public string Blueprint;
        public bool Created;
        public string Platform;
        public string ContentType;
        public string Stage;
        public string Message;
        public string[] Errors;
        public string[] Warnings;
        public string[] Information;

        public static void Write(
            string resultFile,
            bool success,
            int exitCode,
            string blueprint,
            bool created,
            string platform,
            string contentType,
            string stage,
            string message,
            string[] errors = null,
            string[] warnings = null,
            string[] information = null)
        {
            DeploymentResult result = new DeploymentResult
            {
                Success = success,
                ExitCode = exitCode,
                Blueprint = blueprint,
                Created = created,
                Platform = platform,
                ContentType = contentType,
                Stage = stage,
                Message = message,
                Errors = errors,
                Warnings = warnings,
                Information = information
            };

            string directory = Path.GetDirectoryName(resultFile);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(resultFile, JsonUtility.ToJson(result, true));
        }
    }
}
