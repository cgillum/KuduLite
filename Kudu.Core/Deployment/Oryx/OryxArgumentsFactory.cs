﻿using Kudu.Contracts.Settings;
using Kudu.Core.Infrastructure;

namespace Kudu.Core.Deployment.Oryx
{
    public class OryxArgumentsFactory
    {
        public static IOryxArguments CreateOryxArguments(IEnvironment env, IDeploymentSettingsManager settingsManager)
        {
            if (FunctionAppHelper.LooksLikeFunctionApp())
            {
                if (env.IsOnLinuxConsumption)
                {
                    return new LinuxConsumptionFunctionAppOryxArguments();
                } else {
                    return new FunctionAppOryxArguments();
                }
            }
            return new AppServiceOryxArguments(settingsManager);
        }
    }
}
