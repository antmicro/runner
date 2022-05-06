using System;
using System.Diagnostics;
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
    }
}
