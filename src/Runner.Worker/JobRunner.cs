﻿using GitHub.DistributedTask.WebApi;
using Pipelines = GitHub.DistributedTask.Pipelines;
using GitHub.Runner.Common.Util;
using GitHub.Services.Common;
using GitHub.Services.WebApi;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using GitHub.Runner.Common;
using GitHub.Runner.Sdk;
using GitHub.DistributedTask.Pipelines.ContextData;

namespace GitHub.Runner.Worker
{
    [ServiceLocator(Default = typeof(JobRunner))]
    public interface IJobRunner : IRunnerService
    {
        Task<TaskResult> RunAsync(Pipelines.AgentJobRequestMessage message, CancellationToken jobRequestCancellationToken);
    }

    public sealed class JobRunner : RunnerService, IJobRunner
    {
        private IJobServerQueue _jobServerQueue;
        private ITempDirectoryManager _tempDirectoryManager;

        public async Task<TaskResult> RunAsync(Pipelines.AgentJobRequestMessage message, CancellationToken jobRequestCancellationToken)
        {
            // Validate parameters.
            Trace.Entering();
            ArgUtil.NotNull(message, nameof(message));
            ArgUtil.NotNull(message.Resources, nameof(message.Resources));
            ArgUtil.NotNull(message.Variables, nameof(message.Variables));
            ArgUtil.NotNull(message.Steps, nameof(message.Steps));
            Trace.Info("Job ID {0}", message.JobId);

            DateTime jobStartTimeUtc = DateTime.UtcNow;

            ServiceEndpoint systemConnection = message.Resources.Endpoints.Single(x => string.Equals(x.Name, WellKnownServiceEndpointNames.SystemVssConnection, StringComparison.OrdinalIgnoreCase));

            // Run QEMU
            var qemuProc = new Process();
            var sshfsProc = new Process();
            var instanceNumber = Environment.GetEnvironmentVariable(Constants.InstanceNumberVariable);
            var virtDir = Path.Combine(
                    new DirectoryInfo(HostContext.GetDirectory(WellKnownDirectory.Root)).Parent.FullName,
                    "virt");
            var ghJson = message.ContextData["github"].ToJToken();
            string virtIp = $"172.17.{instanceNumber}.2";

            Trace.Info($"Runner instance: {instanceNumber}");

            Trace.Info($"QEMU tools directory: {virtDir}");

            Trace.Info($"Job container: {message.JobContainer}");

            var repoFullName = $"{message.ContextData["github"].ToJToken()["repository"]}";
            var repoName = repoFullName.Substring(repoFullName.LastIndexOf('/') + 1);
            Trace.Info($"Full repo name: {repoFullName}");
            Trace.Info($"Repo name: {repoName}");

            var PipelineDirectory = repoName.ToString(CultureInfo.InvariantCulture);
            var WorkspaceDirectory = Path.Combine(PipelineDirectory, repoName);

            Trace.Info($"PipelineDirectory: {PipelineDirectory}");
            Trace.Info($"WorkspaceDirectory: {WorkspaceDirectory}");

            message.Variables["system.qemuDir"] = virtDir;
            message.Variables["system.qemuIp"] = virtIp;
            message.Variables["system.containerWorkspace"] = WorkspaceDirectory;

            Trace.Info($"QEMU IP: {virtIp}");

            // Setup the job server and job server queue.
            var jobServer = HostContext.GetService<IJobServer>();
            VssCredentials jobServerCredential = VssUtil.GetVssCredential(systemConnection);
            Uri jobServerUrl = systemConnection.Url;

            Trace.Info($"Creating job server with URL: {jobServerUrl}");
            // jobServerQueue is the throttling reporter.
            _jobServerQueue = HostContext.GetService<IJobServerQueue>();
            VssConnection jobConnection = VssUtil.CreateConnection(jobServerUrl, jobServerCredential, new DelegatingHandler[] { new ThrottlingReportHandler(_jobServerQueue) });
            await jobServer.ConnectAsync(jobConnection);

            _jobServerQueue.Start(message);
            HostContext.WritePerfCounter($"WorkerJobServerQueueStarted_{message.RequestId.ToString()}");

            IExecutionContext jobContext = null;
            CancellationTokenRegistration? runnerShutdownRegistration = null;
            try
            {
                // Create the job execution context.
                jobContext = HostContext.CreateService<IExecutionContext>();
                jobContext.InitializeJob(message, jobRequestCancellationToken);
                Trace.Info("Starting the job execution context.");
                jobContext.Start();
                var githubContext = jobContext.ExpressionValues["github"] as GitHubContext;

                var templateEval = jobContext.ToPipelineTemplateEvaluator();
                var container = templateEval.EvaluateJobContainer(message.JobContainer, jobContext.ExpressionValues, jobContext.ExpressionFunctions);
                
                IExecutionContext qemuCtx = jobContext.CreateChild(Guid.NewGuid(), "Set up VM", "VM_Init", null, null);
                qemuCtx.Start();

                Trace.Info($"Container: ${container.Image}");

                var jobContainerFile = container.Image.Replace(":", "_");

                Trace.Info($"ContainerFile: {jobContainerFile}");

                qemuProc.StartInfo.FileName = WhichUtil.Which("bash", trace: Trace);
                qemuProc.StartInfo.Arguments = $"run_image.sh -n {instanceNumber} -s {jobContainerFile}";
                qemuProc.StartInfo.WorkingDirectory = virtDir;
                qemuProc.StartInfo.UseShellExecute = false;
                qemuProc.StartInfo.RedirectStandardError = true;
                qemuProc.StartInfo.RedirectStandardOutput = true;

                sshfsProc.StartInfo.FileName = WhichUtil.Which("bash", trace: Trace);
                sshfsProc.StartInfo.Arguments = $"sshfs.sh {instanceNumber} {WorkspaceDirectory}";
                sshfsProc.StartInfo.WorkingDirectory = virtDir;
                sshfsProc.StartInfo.UseShellExecute = false;
                sshfsProc.StartInfo.RedirectStandardError = true;
                sshfsProc.StartInfo.RedirectStandardOutput = true;

                // Setup TEMP directories
                _tempDirectoryManager = HostContext.GetService<ITempDirectoryManager>();
                _tempDirectoryManager.InitializeTempDirectory(jobContext);

                qemuProc.Start();
                Trace.Info($"Starting QEMU with start script PID {qemuProc.Id}");

                var qemuToGithub = true;

                using (StreamReader qemuOut = qemuProc.StandardOutput)
                {
                    string line;
                    while((line = qemuOut.ReadLine()) != null)
                    {
                        if (line.Contains("DEBUG START"))
                        {
                            qemuToGithub = false;
                        }

                        if (qemuToGithub)
                        {
                            qemuCtx.Output(line);
                        }

                        Trace.Info(line);
                    }
                }

                qemuProc.WaitForExit();
                Trace.Info("QEMU is ready.");
                qemuCtx.Complete();

                if (qemuProc.ExitCode != 0)
                {
                    var qemuNonZeroExitCode = $"VM starter exited with non-zero exit code: {qemuProc.ExitCode}";
                    qemuCtx.Output(qemuNonZeroExitCode);
                    Trace.Info(qemuNonZeroExitCode);
                    jobContext.Error(qemuNonZeroExitCode);

                    using (StreamReader qemuErr = qemuProc.StandardError)
                    {
                        string line;
                        while((line = qemuErr.ReadLine()) != null)
                        {
                            Trace.Info(line);
                        }
                    }

                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }



                Trace.Info($"Mounting {WorkspaceDirectory} via sshfs...");
                sshfsProc.Start();

                using (StreamReader sshfsOut = sshfsProc.StandardOutput)
                {
                    Trace.Info(sshfsOut.ReadToEnd());
                }

                sshfsProc.WaitForExit();

                if (sshfsProc.ExitCode != 0)
                {
                    string sshfsError;
                    using (StreamReader sshfsErr = sshfsProc.StandardError)
                    {
                        sshfsError = sshfsErr.ReadToEnd();
                    }

                    Trace.Error($"sshfs started exited with {sshfsProc.ExitCode}");
                    Trace.Error(sshfsError);
                    jobContext.Error($"sshfs: exit code {sshfsProc.ExitCode}, err {sshfsError}");

                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }

                if (!JobPassesSecurityRestrictions(jobContext))
                {
                    jobContext.Error("Running job on this worker disallowed by security policy");
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }

                jobContext.Debug($"Starting: {message.JobDisplayName}");

                runnerShutdownRegistration = HostContext.RunnerShutdownToken.Register(() =>
                {
                    // log an issue, then runner get shutdown by Ctrl-C or Ctrl-Break.
                    // the server will use Ctrl-Break to tells the runner that operating system is shutting down.
                    string errorMessage;
                    switch (HostContext.RunnerShutdownReason)
                    {
                        case ShutdownReason.UserCancelled:
                            errorMessage = "The runner has received a shutdown signal. This can happen when the runner service is stopped, or a manually started runner is canceled.";
                            break;
                        case ShutdownReason.OperatingSystemShutdown:
                            errorMessage = $"Operating system is shutting down for computer '{Environment.MachineName}'";
                            break;
                        default:
                            throw new ArgumentException(HostContext.RunnerShutdownReason.ToString(), nameof(HostContext.RunnerShutdownReason));
                    }
                    jobContext.AddIssue(new Issue() { Type = IssueType.Error, Message = errorMessage });
                });

                // Validate directory permissions.
                string workDirectory = HostContext.GetDirectory(WellKnownDirectory.Work);
                Trace.Info($"Validating directory permissions for: '{workDirectory}'");
                try
                {
                    Directory.CreateDirectory(workDirectory);
                    IOUtil.ValidateExecutePermission(workDirectory);
                }
                catch (Exception ex)
                {
                    Trace.Error(ex);
                    jobContext.Error(ex);
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }

                if (jobContext.Global.WriteDebug)
                {
                    jobContext.SetRunnerContext("debug", "1");
                }

                jobContext.SetRunnerContext("os", VarUtil.OS);

                string toolsDirectory = HostContext.GetDirectory(WellKnownDirectory.Tools);
                Directory.CreateDirectory(toolsDirectory);
                jobContext.SetRunnerContext("tool_cache", toolsDirectory);

                // Get the job extension.
                Trace.Info("Getting job extension.");
                IJobExtension jobExtension = HostContext.CreateService<IJobExtension>();
                List<IStep> jobSteps = null;
                try
                {
                    Trace.Info("Initialize job. Getting all job steps.");
                    jobSteps = await jobExtension.InitializeJob(jobContext, message);
                }
                catch (OperationCanceledException ex) when (jobContext.CancellationToken.IsCancellationRequested)
                {
                    // set the job to canceled
                    // don't log error issue to job ExecutionContext, since server owns the job level issue
                    Trace.Error($"Job is canceled during initialize.");
                    Trace.Error($"Caught exception: {ex}");
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Canceled);
                }
                catch (Exception ex)
                {
                    // set the job to failed.
                    // don't log error issue to job ExecutionContext, since server owns the job level issue
                    Trace.Error($"Job initialize failed.");
                    Trace.Error($"Caught exception from {nameof(jobExtension.InitializeJob)}: {ex}");
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }

                // trace out all steps
                Trace.Info($"Total job steps: {jobSteps.Count}.");
                Trace.Verbose($"Job steps: '{string.Join(", ", jobSteps.Select(x => x.DisplayName))}'");
                HostContext.WritePerfCounter($"WorkerJobInitialized_{message.RequestId.ToString()}");

                // Run all job steps
                Trace.Info("Run all job steps.");
                var stepsRunner = HostContext.GetService<IStepsRunner>();
                try
                {
                    foreach (var step in jobSteps)
                    {
                        jobContext.JobSteps.Enqueue(step);
                    }

                    await stepsRunner.RunAsync(jobContext);
                }
                catch (Exception ex)
                {
                    // StepRunner should never throw exception out.
                    // End up here mean there is a bug in StepRunner
                    // Log the error and fail the job.
                    Trace.Error($"Caught exception from job steps {nameof(StepsRunner)}: {ex}");
                    jobContext.Error(ex);
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }
                finally
                {
                    Trace.Info("Finalize job.");
                    jobExtension.FinalizeJob(jobContext, message, jobStartTimeUtc);
                }

                Trace.Info($"Job result after all job steps finish: {jobContext.Result ?? TaskResult.Succeeded}");

                Trace.Info("Completing the job execution context.");
                return await CompleteJobAsync(jobServer, jobContext, message);
            }
            finally
            {
                Trace.Info("Entering finally block.");
                if (runnerShutdownRegistration != null)
                {
                    runnerShutdownRegistration.Value.Dispose();
                    runnerShutdownRegistration = null;
                }

                string virtPid = "", virtPidPath = Path.Combine(virtDir, "work", $"{instanceNumber}_qemu.pid");

                try
                {
                    using (StreamReader reader = new StreamReader(new FileStream(virtPidPath, FileMode.Open)))
                    {
                        virtPid = reader.ReadLine();
                    }
                }
                catch (IOException e)
                {
                    Trace.Error("Reading QEMU work files failed, consult the exception below.");
                    Trace.Error(e.ToString());
                }

                var umountProc = new Process();
                umountProc.StartInfo.FileName = WhichUtil.Which("bash", trace: Trace);
                umountProc.StartInfo.Arguments = $"-e sshfs.sh {instanceNumber} {WorkspaceDirectory}";
                umountProc.StartInfo.WorkingDirectory = virtDir;
                umountProc.StartInfo.UseShellExecute = false;
                umountProc.StartInfo.RedirectStandardError = true;
                umountProc.StartInfo.RedirectStandardOutput = true;
                
                var killProc = new Process();
                killProc.StartInfo.FileName = WhichUtil.Which("kill", trace: Trace);
                killProc.StartInfo.Arguments = virtPid;
                killProc.StartInfo.WorkingDirectory = virtDir;
                killProc.StartInfo.UseShellExecute = false;

                var killProc2 = new Process();
                killProc2.StartInfo.FileName = WhichUtil.Which("kill", trace: Trace);
                killProc2.StartInfo.Arguments = $"-s SIGKILL {virtPid}";
                killProc2.StartInfo.WorkingDirectory = virtDir;
                killProc2.StartInfo.UseShellExecute = false;

                Trace.Info($"Unmouting sshfs from {WorkspaceDirectory}");
                umountProc.Start();
                umountProc.WaitForExit();

                if (umountProc.ExitCode != 0)
                {
                    string umountError;
                    using (StreamReader umountErr = umountProc.StandardError)
                    {
                        umountError = umountErr.ReadToEnd();
                    }

                    Trace.Error(umountError);
                    jobContext.Error(umountError);
                }

                Trace.Info($"Killing QEMU with PID {virtPid}");
                killProc.Start();
                killProc.WaitForExit();

                var waitPid = 5;

                for (int i = 1; i <= waitPid; i++)
                {
                    Trace.Info($"[{i}/{waitPid}] waiting for QEMU to die");
                    if (!File.Exists(virtPidPath))
                    {
                        break;
                    }

                    if (i == waitPid && File.Exists(virtPidPath))
                    {
                        Trace.Info($"Sending SIGKILL to {virtPid}");
                        killProc2.Start();
                        killProc2.WaitForExit();
                        try
                        {
                            File.Delete(virtPidPath);
                            Trace.Info($"Removed {virtPidPath}");
                        }
                        catch (Exception e)
                        {
                            Trace.Info($"Couldn't remove {virtPidPath}: {e}");
                        }
                    }
                    Thread.Sleep(1000);
                }

                await ShutdownQueue(throwOnFailure: false);
            }
        }

        private bool JobPassesSecurityRestrictions(IExecutionContext jobContext)
        {
            var gitHubContext = jobContext.ExpressionValues["github"] as GitHubContext;

            try {
              if (gitHubContext.IsPullRequest())
              {
                  return OkayToRunPullRequest(gitHubContext);
              }

              return true;
            }
            catch (Exception ex)
            {
                Trace.Error("Caught exception in JobPassesSecurityRestrictions");
                Trace.Error("As a safety precaution we are not allowing this job to run");
                Trace.Error(ex);
                return false;
            }
        }

        private bool OkayToRunPullRequest(GitHubContext gitHubContext)
        {
            var configStore = HostContext.GetService<IConfigurationStore>();
            var settings = configStore.GetSettings();
            var prSecuritySettings = settings.PullRequestSecuritySettings;

            if (prSecuritySettings is null) {
                Trace.Info("No pullRequestSecurity defined in settings, allowing this build");
                return true;
            }

            var githubEvent = gitHubContext["event"] as DictionaryContextData;
            var prData = githubEvent["pull_request"] as DictionaryContextData;

            var authorAssociation = prData.TryGetValue("author_association", out var value)
              ? value as StringContextData : null;


            // TODO: Allow COLLABORATOR, MEMBER too -- possibly by a config setting
            if (authorAssociation == "OWNER")
            {
                Trace.Info("PR is from the repo owner, always allowed");
                return true;
            }
            else if (prSecuritySettings.AllowContributors && authorAssociation == "COLLABORATOR") {
                Trace.Info("PR is from the repo collaborator, allowing");
                return true;
            }

            var prHead = prData["head"] as DictionaryContextData;
            var prUser = prHead["user"] as DictionaryContextData;
            var prUserLogin = prUser["login"] as StringContextData;

            Trace.Info($"GitHub PR author is {prUserLogin as StringContextData}");

            if (prUserLogin == null)
            {
                Trace.Info("Unable to get PR author, not allowing PR to run");
                return false;
            }

            if (prSecuritySettings.AllowedAuthors.Contains(prUserLogin))
            {
                Trace.Info("Author in PR allowed list");
                return true;
            }
            else
            {
                Trace.Info($"Not running job as author ({prUserLogin}) is not in {{{string.Join(", ", prSecuritySettings.AllowedAuthors)}}}");

                return false;
            }
        }

        private async Task<TaskResult> CompleteJobAsync(IJobServer jobServer, IExecutionContext jobContext, Pipelines.AgentJobRequestMessage message, TaskResult? taskResult = null)
        {
            jobContext.Debug($"Finishing: {message.JobDisplayName}");
            TaskResult result = jobContext.Complete(taskResult);

            try
            {
                await ShutdownQueue(throwOnFailure: true);
            }
            catch (Exception ex)
            {
                Trace.Error($"Caught exception from {nameof(JobServerQueue)}.{nameof(_jobServerQueue.ShutdownAsync)}");
                Trace.Error("This indicate a failure during publish output variables. Fail the job to prevent unexpected job outputs.");
                Trace.Error(ex);
                result = TaskResultUtil.MergeTaskResults(result, TaskResult.Failed);
            }

            // Clean TEMP after finish process jobserverqueue, since there might be a pending fileupload still use the TEMP dir.
            _tempDirectoryManager?.CleanupTempDirectory();

            if (!jobContext.Global.Features.HasFlag(PlanFeatures.JobCompletedPlanEvent))
            {
                Trace.Info($"Skip raise job completed event call from worker because Plan version is {message.Plan.Version}");
                return result;
            }

            Trace.Info("Raising job completed event.");
            var jobCompletedEvent = new JobCompletedEvent(message.RequestId, message.JobId, result, jobContext.JobOutputs, jobContext.ActionsEnvironment);

            var completeJobRetryLimit = 5;
            var exceptions = new List<Exception>();
            while (completeJobRetryLimit-- > 0)
            {
                try
                {
                    await jobServer.RaisePlanEventAsync(message.Plan.ScopeIdentifier, message.Plan.PlanType, message.Plan.PlanId, jobCompletedEvent, default(CancellationToken));
                    return result;
                }
                catch (TaskOrchestrationPlanNotFoundException ex)
                {
                    Trace.Error($"TaskOrchestrationPlanNotFoundException received, while attempting to raise JobCompletedEvent for job {message.JobId}.");
                    Trace.Error(ex);
                    return TaskResult.Failed;
                }
                catch (TaskOrchestrationPlanSecurityException ex)
                {
                    Trace.Error($"TaskOrchestrationPlanSecurityException received, while attempting to raise JobCompletedEvent for job {message.JobId}.");
                    Trace.Error(ex);
                    return TaskResult.Failed;
                }
                catch (TaskOrchestrationPlanTerminatedException ex)
                {
                    Trace.Error($"TaskOrchestrationPlanTerminatedException received, while attempting to raise JobCompletedEvent for job {message.JobId}.");
                    Trace.Error(ex);
                    return TaskResult.Failed;
                }
                catch (Exception ex)
                {
                    Trace.Error($"Catch exception while attempting to raise JobCompletedEvent for job {message.JobId}, job request {message.RequestId}.");
                    Trace.Error(ex);
                    exceptions.Add(ex);
                }

                // delay 5 seconds before next retry.
                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            // rethrow exceptions from all attempts.
            throw new AggregateException(exceptions);
        }

        private async Task ShutdownQueue(bool throwOnFailure)
        {
            if (_jobServerQueue != null)
            {
                try
                {
                    Trace.Info("Shutting down the job server queue.");
                    await _jobServerQueue.ShutdownAsync();
                }
                catch (Exception ex) when (!throwOnFailure)
                {
                    Trace.Error($"Caught exception from {nameof(JobServerQueue)}.{nameof(_jobServerQueue.ShutdownAsync)}");
                    Trace.Error(ex);
                }
                finally
                {
                    _jobServerQueue = null; // Prevent multiple attempts.
                }
            }
        }
    }
}
