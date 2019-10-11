﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Kudu.Console.Services;
using Kudu.Contracts.Infrastructure;
using Kudu.Contracts.Settings;
using Kudu.Contracts.Tracing;
using Kudu.Core;
using Kudu.Core.Deployment;
using Kudu.Core.Deployment.Generator;
using Kudu.Core.Helpers;
using Kudu.Core.Hooks;
using Kudu.Core.Infrastructure;
using Kudu.Core.Settings;
using Kudu.Core.SourceControl;
using Kudu.Core.SourceControl.Git;
using Kudu.Core.Tracing;
using System.Reflection;
using XmlSettings;
using k8s;
using k8s.Models;
using Microsoft.AspNetCore.JsonPatch;
using LibGit2Sharp;
using System.Threading;
using IRepository = Kudu.Core.SourceControl.IRepository;
using log4net;
using log4net.Config;

namespace Kudu.Console
{
    internal class Program
    {
        private static IEnvironment env;
        private static IDeploymentSettingsManager settingsManager;
        private static string appRoot;

        private static int Main(string[] args)
        {
            var logRepository = LogManager.GetRepository(Assembly.GetEntryAssembly());
            XmlConfigurator.Configure(logRepository, new FileInfo("log4net.config"));
            // Turn flag on in app.config to wait for debugger on launch
            if (ConfigurationManager.AppSettings["WaitForDebuggerOnStart"] == "true")
            {
                while (!Debugger.IsAttached)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }

            if (System.Environment.GetEnvironmentVariable(SettingsKeys.DisableDeploymentOnPush) == "1")
            {
                return 0;
            }

            if (args.Length < 2)
            {
                System.Console.WriteLine("Usage: kudu.exe appRoot wapTargets [deployer]");
                return 1;
            }

            // The post receive hook launches the exe from sh and intereprets newline differently.
            // This fixes very wacky issues with how the output shows up in the console on push
            System.Console.Error.NewLine = "\n";
            System.Console.Out.NewLine = "\n";

            appRoot = args[0];
            string wapTargets = args[1];
            string deployer = args.Length == 2 ? null : args[2];
            string requestId = System.Environment.GetEnvironmentVariable(Constants.RequestIdHeader);

            env = GetEnvironment(appRoot, requestId);
            ISettings settings = new XmlSettings.Settings(GetSettingsPath(env));
            settingsManager = new DeploymentSettingsManager(settings);

            // Setup the trace
            TraceLevel level = settingsManager.GetTraceLevel();
            ITracer tracer = GetTracer(env, level);
            ITraceFactory traceFactory = new TracerFactory(() => tracer);

            // Calculate the lock path
            string lockPath = Path.Combine(env.SiteRootPath, Constants.LockPath);
            string deploymentLockPath = Path.Combine(lockPath, Constants.DeploymentLockFile);

            IOperationLock deploymentLock = DeploymentLockFile.GetInstance(deploymentLockPath, traceFactory);
            
            if (deploymentLock.IsHeld)
            {
                return PerformDeploy(appRoot, wapTargets, deployer, lockPath, env, settingsManager, level, tracer, traceFactory, deploymentLock);
            }

            // Cross child process lock is not working on linux via mono.
            // When we reach here, deployment lock must be HELD! To solve above issue, we lock again before continue.
            try
            {
                return deploymentLock.LockOperation(() =>
                {
                    return PerformDeploy(appRoot, wapTargets, deployer, lockPath, env, settingsManager, level, tracer, traceFactory, deploymentLock);
                }, "Performing deployment", TimeSpan.Zero);
            }
            catch (LockOperationException)
            {
                return -1;
            }
        }

        private static int PerformDeploy(
            string appRoot,
            string wapTargets,
            string deployer,
            string lockPath,
            IEnvironment env,
            IDeploymentSettingsManager settingsManager,
            TraceLevel level,
            ITracer tracer,
            ITraceFactory traceFactory,
            IOperationLock deploymentLock)
        {
            System.Environment.SetEnvironmentVariable("GIT_DIR", null, System.EnvironmentVariableTarget.Process);

            // Skip SSL Certificate Validate
            if (System.Environment.GetEnvironmentVariable(SettingsKeys.SkipSslValidation) == "1")
            {
                ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            }

            // Adjust repo path
            env.RepositoryPath = Path.Combine(env.SiteRootPath, settingsManager.GetRepositoryPath());

            string statusLockPath = Path.Combine(lockPath, Constants.StatusLockFile);
            string hooksLockPath = Path.Combine(lockPath, Constants.HooksLockFile);

            
            IOperationLock statusLock = new LockFile(statusLockPath, traceFactory);
            IOperationLock hooksLock = new LockFile(hooksLockPath, traceFactory);
            
            IBuildPropertyProvider buildPropertyProvider = new BuildPropertyProvider();
            ISiteBuilderFactory builderFactory = new SiteBuilderFactory(buildPropertyProvider, env);
            var logger = new ConsoleLogger();

            IRepository gitRepository;
            if (settingsManager.UseLibGit2SharpRepository())
            {
                gitRepository = new LibGit2SharpRepository(env, settingsManager, traceFactory);
            }
            else
            {
                gitRepository = new GitExeRepository(env, settingsManager, traceFactory);
            }
            env.CurrId = gitRepository.GetChangeSet(settingsManager.GetBranch()).Id;

            IServerConfiguration serverConfiguration = new ServerConfiguration();
            IAnalytics analytics = new Analytics(settingsManager, serverConfiguration, traceFactory);

            IWebHooksManager hooksManager = new WebHooksManager(tracer, env, hooksLock);
            IDeploymentStatusManager deploymentStatusManager = new DeploymentStatusManager(env, analytics, statusLock);
            IDeploymentManager deploymentManager = new DeploymentManager(builderFactory,
                                                          env,
                                                          traceFactory,
                                                          analytics,
                                                          settingsManager,
                                                          deploymentStatusManager,
                                                          deploymentLock,
                                                          GetLogger(env, level, logger),
                                                          hooksManager);
            var step = tracer.Step(XmlTracer.ExecutingExternalProcessTrace, new Dictionary<string, string>
            {
                { "type", "process" },
                { "path", "kudu.exe" },
                { "arguments", appRoot + " " + wapTargets }
            });

            using (step)
            {
                try
                {
                    // although the api is called DeployAsync, most expensive works are done synchronously.
                    // need to launch separate task to go async explicitly (consistent with FetchDeploymentManager)
                    var deploymentTask = Task.Run(async () => await deploymentManager.DeployAsync(gitRepository, changeSet: null, deployer: deployer, clean: false));

#pragma warning disable 4014
                    // Track pending task
                    PostDeploymentHelper.TrackPendingOperation(deploymentTask, TimeSpan.Zero);
#pragma warning restore 4014

                    deploymentTask.Wait();

                    if (PostDeploymentHelper.IsAutoSwapEnabled())
                    {
                        string branch = settingsManager.GetBranch();
                        ChangeSet changeSet = gitRepository.GetChangeSet(branch);
                        IDeploymentStatusFile statusFile = deploymentStatusManager.Open(changeSet.Id);
                        if (statusFile != null && statusFile.Status == DeployStatus.Success)
                        {
                            PostDeploymentHelper.PerformAutoSwap(env.RequestId,
                                    new PostDeploymentTraceListener(tracer, deploymentManager.GetLogger(changeSet.Id)))
                                .Wait();
                        }
                    }
                }
                catch (Exception e)
                {
                    tracer.TraceError(e);
                    System.Console.Error.WriteLine(e.GetBaseException().Message);
                    System.Console.Error.WriteLine(Resources.Log_DeploymentError);
                    return 1;
                }
                finally
                {
                    var config = KubernetesClientConfiguration.InClusterConfig();
                    var client = new Kubernetes(config);
                    var replicaSets = client.ListReplicaSetForAllNamespaces();
                    string appName = appRoot.Replace("/home/apps/", "").Split("/")[0];

                    foreach (var replicaSet in replicaSets.Items)
                    {
                        System.Console.WriteLine(" Replica Set name : "+ replicaSet.Metadata.Name);
                        System.Console.WriteLine(" Replica Set avail : " + replicaSet.Status.AvailableReplicas);
                        if (replicaSet.Metadata.Name.Equals(appName, StringComparison.OrdinalIgnoreCase)
                            && (replicaSet.Status.AvailableReplicas > 0))
                        {
                            System.Console.WriteLine("Revision Number: "+replicaSet.Metadata.Annotations["deployment.kubernetes.io/revision"]);
                        }
                    }
                    /*
                    // Load from in-cluster configuration:
                    var config = KubernetesClientConfiguration.InClusterConfig();
                    //var config = KubernetesClientConfiguration.InClusterConfig();

                    // Use the config object to create a client.
                    var client = new Kubernetes(config);
                    try
                    {
                        var deployments = client.ListDeploymentForAllNamespaces();
                        //client.PatchNamespacedDeployment1('{"spec":{"template":{"spec":{"containers":[{"name":"nginx","image":"nginx:1.11"}]}}}}');
                        
                        foreach (var deployment in deployments.Items)
                        {
                            if(deployment.Spec.Template.Metadata.Name.Equals("second"))
                            {
                                foreach (var container in deployment.Spec.Template.Spec.Containers)
                                {
                                    foreach (var volumeMount in container.VolumeMounts)
                                    {
                                        volumeMount.SubPath = "apps/kudutestmount";
                                        client.PatchNamespacedDeployment(new V1Patch<V1Deployment>(deployment), "second", "default");
                                    }
                                }
                                var jdoc = new JsonPatchDocument<V1Deployment>();
                                //jdoc.Add("spec/template/spec/containers/volumeMounts/mountPath/subPath", "apps/kudutestmount");
                            }
                        }
                    }
                    catch (Microsoft.Rest.HttpOperationException httpOperationException)
                    {
                        var phase = httpOperationException.Response.ReasonPhrase;
                        //Bad Request
                        var content = httpOperationException.Response.Content;
                        System.Console.WriteLine("K8 Client errror");
                        System.Console.WriteLine(content);
                    }
                    */
                    // IEnvironment environment, IDeploymentSettingsManager settings, IBuildPropertyProvider propertyProvider, string repositoryPath
                    //string appName = appRoot.Replace("/home/apps/", "").Split("/")[0];

                    System.Console.WriteLine("Restarting Pods for App Service App : " + appName);
                    System.Console.WriteLine($" Patch Args :::::: -c \" /patch.sh {appName} apps/{appName}/site/artifacts/{gitRepository.GetChangeSet(settingsManager.GetBranch()).Id}\"");

                    Process _executingProcess = new Process()
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "/bin/bash",
                            Arguments = $"-c \" /patch.sh {appName} apps/{appName}/site/artifacts/{gitRepository.GetChangeSet(settingsManager.GetBranch()).Id}\"",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        }
                    };
                    // Read the standard error of net.exe and write it on to console.
                    _executingProcess.OutputDataReceived += (sender, args) => System.Console.WriteLine("{0}", args.Data);
                    _executingProcess.Start();
                    //* Read the output (or the error)
                    //string output = _executingProcess.StandardOutput.ReadToEnd();
                    //System.Console.WriteLine(output);
                    //string err = _executingProcess.StandardError.ReadToEnd();
                    //System.Console.WriteLine(err);
                    _executingProcess.WaitForExit();
                    System.Console.WriteLine("Process exit code : "+_executingProcess.ExitCode);
                    System.Console.WriteLine("All Pods Restarted!");

                    //ExternalCommandBuilder restartApp = new ExternalCommandBuilder(env, settingsManager, buildPropertyProvider,env.RepositoryPath);

                    System.Console.WriteLine("Deployment Logs : '"+
                    env.AppBaseUrlPrefix+ "/newui/jsonviewer?view_url=/api/deployments/" + 
                    gitRepository.GetChangeSet(settingsManager.GetBranch()).Id+"/log'");
                }
            }

            if (logger.HasErrors)
            {
                return 1;
            }
            tracer.Step("Perform deploy exiting successfully");
            return 0;
        }

        private static ITracer GetTracer(IEnvironment env, TraceLevel level)
        {
            if (level > TraceLevel.Off)
            {
                var tracer = new XmlTracer(env.TracePath, level);
                string logFile = System.Environment.GetEnvironmentVariable(Constants.TraceFileEnvKey);
                if (!String.IsNullOrEmpty(logFile))
                {
                    // Kudu.exe is executed as part of git.exe (post-receive), giving its initial depth of 4 indentations
                    string logPath = Path.Combine(env.TracePath, logFile);
                    // since git push is "POST", which then run kudu.exe
                    return new CascadeTracer(tracer, new TextTracer(logPath, level, 4), new ETWTracer(env.RequestId, requestMethod: HttpMethod.Post.Method));
                }

                return tracer;
            }

            return NullTracer.Instance;
        }

        private static ILogger GetLogger(IEnvironment env, TraceLevel level, ILogger primary)
        {
            if (level > TraceLevel.Off)
            {
                string logFile = System.Environment.GetEnvironmentVariable(Constants.TraceFileEnvKey);
                if (!String.IsNullOrEmpty(logFile))
                {
                    string logPath = Path.Combine(env.RootPath, Constants.DeploymentTracePath, logFile);
                    //return new CascadeLogger(primary, new TextLogger(logPath));
                    return new CascadeLogger(primary, new TextLogger(logPath));
                }
            }

            return primary;
        }

        private static string GetSettingsPath(IEnvironment environment)
        {
            return Path.Combine(environment.DeploymentsPath, Constants.DeploySettingsPath);
        }

        private static IEnvironment GetEnvironment(string siteRoot, string requestId)
        {
            string root = Path.GetFullPath(Path.Combine(siteRoot, ".."));

            // CORE TODO : test by setting SCM_REPOSITORY_PATH 
            // REVIEW: this looks wrong because it ignores SCM_REPOSITORY_PATH
            string repositoryPath = Path.Combine(siteRoot, Constants.RepositoryPath);

            // SCM_BIN_PATH is introduced in Kudu apache config file 
            // Provide a way to override Kudu bin path, to resolve issue where we can not find the right Kudu bin path when running on mono
            // CORE TODO I don't think this is needed anymore? This env var is not used anywhere but here.
            string binPath = System.Environment.GetEnvironmentVariable("SCM_BIN_PATH");
            if (string.IsNullOrWhiteSpace(binPath))
            {
                // CORE TODO Double check. Process.GetCurrentProcess() always gets the dotnet.exe process,
                // so changed to Assembly.GetEntryAssembly().Location
                binPath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            }

            // CORE TODO Handing in a null IHttpContextAccessor (and KuduConsoleFullPath) again
            var env=  new Kudu.Core.Environment(root,
                EnvironmentHelper.NormalizeBinPath(binPath),
                repositoryPath,
                requestId,
                Path.Combine(AppContext.BaseDirectory, "KuduConsole", "kudu.dll"),
                null);
            return env;
        }
    }
}