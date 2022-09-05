using System;
using System.Diagnostics;
using System.Collections.Generic;
using GitHub.Runner.Sdk;
using GitHub.Runner.Common;

namespace GitHub.Runner.GCP
{
    public sealed class GCPRunner {
        public static string TranslateToGCPRunnerPath(string path) {
            var instanceNumber = System.Environment.GetEnvironmentVariable(Constants.InstanceNumberVariable);
            return path.Replace($"/home/runner/github-actions-runner/_layout/_work_{instanceNumber}/", "/root/");
        }
    }

    public sealed class GCPCoordinator {
        public static string TranslateToGCPCoordinatorPath(string path) {
            var instanceNumber = System.Environment.GetEnvironmentVariable(Constants.InstanceNumberVariable);
            return path.Replace("/root/", $"/home/runner/github-actions-runner/_layout/_work_{instanceNumber}/");
        }

        public static int RunProcess(string fileName, string arguments, string workDirectory, DataReceivedEventHandler outputDataReceivedFunc, DataReceivedEventHandler errorDataReceivedFunc, Action<Exception> exceptionFunc, ITraceWriter trace = null, int exceptionReturnCode = 255) {
            try {
                var proc = new Process();
                proc.StartInfo.FileName = WhichUtil.Which(fileName, trace: trace);
                proc.StartInfo.Arguments = arguments;
                proc.StartInfo.WorkingDirectory = workDirectory;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.RedirectStandardError = true;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.OutputDataReceived += outputDataReceivedFunc;
                proc.ErrorDataReceived += errorDataReceivedFunc;
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                return proc.ExitCode;
            } catch (Exception e) {
                exceptionFunc(e);
                return exceptionReturnCode;
            }
        }

        public static int SynchronizeCoordinatorFiles(IHostContext hostContext) {
            var trace = hostContext.GetTrace(nameof(HostContext));
            var sshIp = System.Environment.GetEnvironmentVariable(Constants.RunnerIPVariable);
            // We need to append '/' at the end of the path to only sync content of the
            // work directory instead of the work directory itself
            var syncPath = hostContext.GetDirectory(WellKnownDirectory.Work) + "/";
            var rsyncArguments = new List<string>(Constants.CommonRsyncArgs);
            rsyncArguments.Add($"{syncPath} scalerunner@{sshIp}:/mnt/2");
            trace.Info($"Sync: {syncPath} to /mnt/2");
            return GCPCoordinator.RunProcess(
                    fileName: "rsync",
                    arguments: string.Join(" ", rsyncArguments.ToArray()), 
                    workDirectory: "",
                    outputDataReceivedFunc: (_, args) => trace.Info(args.Data ?? ""),
                    errorDataReceivedFunc: (_, args) => trace.Info(args.Data ?? ""),
                    exceptionFunc: (e) => { trace.Info("Exception when syncing coordinator files!"); trace.Info(e.Message); },
                    trace: trace,
                    exceptionReturnCode: 255);
        }

        public static int SynchronizeWorkerFiles(IHostContext hostContext) {
            var trace = hostContext.GetTrace(nameof(HostContext));
            var sshIp = System.Environment.GetEnvironmentVariable(Constants.RunnerIPVariable);
            var syncPath = hostContext.GetDirectory(WellKnownDirectory.Work);
            var rsyncArguments = new List<string>(Constants.CommonRsyncArgs);
            rsyncArguments.Add($"scalerunner@{sshIp}:/mnt/2/_temp/ {syncPath}/_temp");
            trace.Entering();
            return GCPCoordinator.RunProcess(
                    fileName: "rsync",
                    arguments: string.Join(" ", rsyncArguments.ToArray()), 
                    workDirectory: "",
                    outputDataReceivedFunc: (_, args) => trace.Info(args.Data ?? ""),
                    errorDataReceivedFunc: (_, args) => trace.Info(args.Data ?? ""),
                    exceptionFunc: (e) => { trace.Info("Exception when syncing worker files!"); trace.Info(e.Message); },
                    trace: trace,
                    exceptionReturnCode: 255);
        }

        public static int SynchronizeActionFiles(IHostContext hostContext, string workerFilePath, string coordinatorFilePath) {
            var trace = hostContext.GetTrace(nameof(HostContext));
            var sshIp = System.Environment.GetEnvironmentVariable(Constants.RunnerIPVariable);
            var syncPath = hostContext.GetDirectory(WellKnownDirectory.Work);
            var rsyncArguments = new List<string>(Constants.CommonRsyncArgs);
            // We want to copy only action files from worker to coordinator
            // from rsync man:
            // As the list of files/directories to transfer is built, rsync checks each name to be transferred
            // against the list of include/exclude patterns in turn, and the first matching pattern is acted on:
            // if it is an exclude pattern, then that file is skipped;
            // if it is an include pattern then that filename is not skipped;
            // if no matching pattern is found, then the filename is not skipped.
            rsyncArguments.Add("--include=\"action.yml\" --include=\"action.yaml\" --include=\"Dockerfile\" --exclude=\"*\"");
            rsyncArguments.Add($"scalerunner@{sshIp}:{workerFilePath.Replace("/root/", "/mnt/2/")}/ {coordinatorFilePath}");
            trace.Entering();
            return GCPCoordinator.RunProcess(
                    fileName: "rsync",
                    arguments: string.Join(" ", rsyncArguments.ToArray()), 
                    workDirectory: "",
                    outputDataReceivedFunc: (_, args) => trace.Info(args.Data ?? ""),
                    errorDataReceivedFunc: (_, args) => trace.Info(args.Data ?? ""),
                    exceptionFunc: (e) => { trace.Info("Exception when syncing worker files!"); trace.Info(e.Message); },
                    trace: trace,
                    exceptionReturnCode: 255);
        }
    }
}
