﻿using System;

namespace Kudu.Core.Helpers
{
    public static class EnvironmentHelper
    {
        public static string NormalizeBinPath(string binPath)
        {
            if (!string.IsNullOrWhiteSpace(binPath) && !OSDetector.IsOnWindows())
            {
                int binIdx = binPath.LastIndexOf("Bin", StringComparison.Ordinal);
                if (binIdx >= 0)
                {
                    string subStr = binPath.Substring(binIdx);
                    // make sure file path is end with ".....Bin" or "....Bin/"
                    if (subStr.Length < 5 && binPath.EndsWith(subStr, StringComparison.OrdinalIgnoreCase))
                    {
                        // real bin folder is lower case, but in mono, value is "Bin" instead of "bin"
                        binPath = binPath.Substring(0, binIdx) + subStr.ToLowerInvariant();
                    }
                }
            }

            return binPath;
        }

        public static bool IsDynamicInstallEnvironment()
        {
            var dynEnvVarValue = System.Environment.GetEnvironmentVariable("ENABLE_DYNAMIC_INSTALL");
            return dynEnvVarValue != null && string.Equals(dynEnvVarValue, "true", StringComparison.OrdinalIgnoreCase);
        }

        // Is this a Windows Containers site?
        public static bool IsWindowsContainers()
        {
            string xenon = System.Environment.GetEnvironmentVariable("XENON");
            int parsedXenon = 0;
            bool isXenon = false;
            if (int.TryParse(xenon, out parsedXenon))
            {
                isXenon = (parsedXenon == 1);
            }
            return isXenon;
        }

        // Check if an app is a Linux Consumption function app
        // This method is similar to
        public static bool IsOnLinuxConsumption()
        {
            bool isOnAppService = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(Constants.AzureWebsiteInstanceId));
            bool isOnLinuxContainer = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(Constants.ContainerName));
            return isOnLinuxContainer && !isOnAppService;
        }
    }
}
