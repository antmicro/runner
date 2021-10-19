using System;
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
    }
}
