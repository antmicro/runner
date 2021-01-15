using System.IO;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using GitHub.Runner.Common;
using GitHub.Runner.Common.Util;
using GitHub.Runner.Sdk;
using GitHub.DistributedTask.WebApi;
using Pipelines = GitHub.DistributedTask.Pipelines;
using System;
using System.Linq;

namespace GitHub.Runner.Worker.Handlers
{
    [ServiceLocator(Default = typeof(NodeScriptActionHandler))]
    public interface INodeScriptActionHandler : IHandler
    {
        NodeJSActionExecutionData Data { get; set; }
    }

    public sealed class NodeScriptActionHandler : Handler, INodeScriptActionHandler
    {
        public NodeJSActionExecutionData Data { get; set; }

        public async Task RunAsync(ActionRunStage stage)
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(Data, nameof(Data));
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            ArgUtil.NotNull(Inputs, nameof(Inputs));
            ArgUtil.Directory(ActionDirectory, nameof(ActionDirectory));

            // Update the env dictionary.
            AddInputsToEnvironment();
            AddPrependPathToEnvironment();

            // expose context to environment
            foreach (var context in ExecutionContext.ExpressionValues)
            {
                if (context.Value is IEnvironmentContextData runtimeContext && runtimeContext != null)
                {
                    foreach (var env in runtimeContext.GetRuntimeEnvironmentVariables())
                    {
                        Environment[env.Key] = env.Value;
                    }
                }
            }

            // Add Actions Runtime server info
            var systemConnection = ExecutionContext.Global.Endpoints.Single(x => string.Equals(x.Name, WellKnownServiceEndpointNames.SystemVssConnection, StringComparison.OrdinalIgnoreCase));
            Environment["ACTIONS_RUNTIME_URL"] = systemConnection.Url.AbsoluteUri;
            Environment["ACTIONS_RUNTIME_TOKEN"] = systemConnection.Authorization.Parameters[EndpointAuthorizationParameters.AccessToken];
            if (systemConnection.Data.TryGetValue("CacheServerUrl", out var cacheUrl) && !string.IsNullOrEmpty(cacheUrl))
            {
                Environment["ACTIONS_CACHE_URL"] = cacheUrl;
            }

            // Resolve the target script.
            string target = null;
            if (stage == ActionRunStage.Main)
            {
                target = Data.Script;
            }
            else if (stage == ActionRunStage.Pre)
            {
                target = Data.Pre;
            }
            else if (stage == ActionRunStage.Post)
            {
                target = Data.Post;
            }

            ArgUtil.NotNullOrEmpty(target, nameof(target));
            target = Path.Combine(ActionDirectory, target);
            ArgUtil.File(target, nameof(target));

            // Resolve the working directory.
            string workingDirectory = ExecutionContext.GetGitHubContext("workspace");
            if (string.IsNullOrEmpty(workingDirectory))
            {
                workingDirectory = HostContext.GetDirectory(WellKnownDirectory.Work);
            }

            Trace.Info($"workspace: {workingDirectory}");

            var actionName = ActionDirectory.Split("_actions/")[1];

            Trace.Info($"Stage: {stage}, target: {target}, action dir: {ActionDirectory}, action name: {actionName}");

            if (actionName == "actions/upload-artifact/v2")
            {
                var instanceNumber = System.Environment.GetEnvironmentVariable(Constants.InstanceNumberVariable);
                var virtDir = Path.Combine(new DirectoryInfo(HostContext.GetDirectory(WellKnownDirectory.Root)).Parent.FullName, "virt");

                var tempDir = HostContext.GetDirectory(WellKnownDirectory.Temp);

                var sargraphStop = new Process();
                sargraphStop.StartInfo.FileName = WhichUtil.Which("bash", trace: Trace);
                sargraphStop.StartInfo.Arguments = $"ssh.sh {instanceNumber} --sargraph-stop";
                sargraphStop.StartInfo.WorkingDirectory = virtDir;
                sargraphStop.StartInfo.UseShellExecute = false;
                sargraphStop.StartInfo.RedirectStandardError = true;
                sargraphStop.StartInfo.RedirectStandardOutput = true;

                sargraphStop.Start();

                var symFix = new Process();
                symFix.StartInfo.FileName = WhichUtil.Which("bash", trace: Trace);
                symFix.StartInfo.Arguments = $"symlink_resolve.sh {instanceNumber}";
                symFix.StartInfo.WorkingDirectory = virtDir;
                symFix.StartInfo.UseShellExecute = false;
                symFix.StartInfo.RedirectStandardError = true;
                symFix.StartInfo.RedirectStandardOutput = true;

                symFix.Start();
                Trace.Info($"Starting ${symFix.StartInfo.Arguments} with PID {symFix.Id}");

                Trace.Info($"{symFix.StartInfo.Arguments} stdout:");
                using (StreamReader o = symFix.StandardOutput)
                {
                    Trace.Info("\n"+o.ReadToEnd()+"\n");
                }

                Trace.Info($"{symFix.StartInfo.Arguments} stderr:");
                using (StreamReader o = symFix.StandardError)
                {
                    Trace.Info("\n"+o.ReadToEnd()+"\n");
                }

                var plotFile = Path.Combine(tempDir, "_runner_file_commands", "plot.svg");

                symFix.WaitForExit();
                sargraphStop.WaitForExit();

                Trace.Info($"{symFix.StartInfo.Arguments} exit code: {symFix.ExitCode}");
                Trace.Info($"{sargraphStop.StartInfo.Arguments} exit code: {sargraphStop.ExitCode}");

                if (sargraphStop.ExitCode == 0)
                {
                    File.Copy(plotFile, Path.Combine(workingDirectory, "plot.svg"));
                }
            }

            var nodeRuntimeVersion = await StepHost.DetermineNodeRuntimeVersion(ExecutionContext);
            string file = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Externals), nodeRuntimeVersion, "bin", $"node{IOUtil.ExeExtension}");

            // Format the arguments passed to node.
            // 1) Wrap the script file path in double quotes.
            // 2) Escape double quotes within the script file path. Double-quote is a valid
            // file name character on Linux.
            string arguments = StepHost.ResolvePathForStepHost(StringUtil.Format(@"""{0}""", target.Replace(@"""", @"\""")));

#if OS_WINDOWS
            // It appears that node.exe outputs UTF8 when not in TTY mode.
            Encoding outputEncoding = Encoding.UTF8;
#else
            // Let .NET choose the default.
            Encoding outputEncoding = null;
#endif

            using (var stdoutManager = new OutputManager(ExecutionContext, ActionCommandManager))
            using (var stderrManager = new OutputManager(ExecutionContext, ActionCommandManager))
            {
                StepHost.OutputDataReceived += stdoutManager.OnDataReceived;
                StepHost.ErrorDataReceived += stderrManager.OnDataReceived;

                // Execute the process. Exit code 0 should always be returned.
                // A non-zero exit code indicates infrastructural failure.
                // Task failure should be communicated over STDOUT using ## commands.
                Task<int> step = StepHost.ExecuteAsync(workingDirectory: StepHost.ResolvePathForStepHost(workingDirectory),
                                                fileName: StepHost.ResolvePathForStepHost(file),
                                                arguments: arguments,
                                                environment: Environment,
                                                requireExitCodeZero: false,
                                                outputEncoding: outputEncoding,
                                                killProcessOnCancel: false,
                                                inheritConsoleHandler: !ExecutionContext.Global.Variables.Retain_Default_Encoding,
                                                cancellationToken: ExecutionContext.CancellationToken);

                // Wait for either the node exit or force finish through ##vso command
                await System.Threading.Tasks.Task.WhenAny(step, ExecutionContext.ForceCompleted);

                if (ExecutionContext.ForceCompleted.IsCompleted)
                {
                    ExecutionContext.Debug("The task was marked as \"done\", but the process has not closed after 5 seconds. Treating the task as complete.");
                }
                else
                {
                    var exitCode = await step;
                    ExecutionContext.Debug($"Node Action run completed with exit code {exitCode}");
                    if (exitCode != 0)
                    {
                        ExecutionContext.Result = TaskResult.Failed;
                    }
                }
            }
        }
    }
}
