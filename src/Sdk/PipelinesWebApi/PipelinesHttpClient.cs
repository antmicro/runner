using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Services.Common;
using GitHub.Services.WebApi;
using Newtonsoft.Json.Linq;

namespace GitHub.Actions.Pipelines.WebApi
{
    [ResourceArea(PipelinesArea.IdString)]
    public class PipelinesHttpClient : PipelinesHttpClientBase
    {
        public PipelinesHttpClient(Uri baseUrl, VssCredentials credentials)
            : base(baseUrl, credentials)
        {
        }

        public PipelinesHttpClient(Uri baseUrl, VssCredentials credentials, VssHttpRequestSettings settings)
            : base(baseUrl, credentials, settings)
        {
        }

        public PipelinesHttpClient(Uri baseUrl, VssCredentials credentials, params DelegatingHandler[] handlers)
            : base(baseUrl, credentials, handlers)
        {
        }

        public PipelinesHttpClient(Uri baseUrl, VssCredentials credentials, VssHttpRequestSettings settings, params DelegatingHandler[] handlers)
            : base(baseUrl, credentials, settings, handlers)
        {
        }

        public PipelinesHttpClient(Uri baseUrl, HttpMessageHandler pipeline, bool disposeHandler)
            : base(baseUrl, pipeline, disposeHandler)
        {
        }

        public Task<JObject> GetJobsAsync(
            Guid planId,
            object userState = null,
            CancellationToken cancellationToken = default)
        {
            HttpMethod httpMethod = new HttpMethod("GET");
            Guid locationId = new Guid("4818972d-29fa-4b86-92c1-de5ae7ef33f5");
            object routeValues = new { planId = planId };

            List<KeyValuePair<string, string>> queryParams = new List<KeyValuePair<string, string>>();

            return SendAsync<JObject>(
                httpMethod,
                locationId,
                routeValues: routeValues,
                version: new ApiResourceVersion(6.0, 1),
                queryParameters: queryParams,
                userState: userState,
                cancellationToken: cancellationToken);
        }

        public Task<JObject> GetSummaryAsync(
            Guid planId,
            Guid jobId,
            object userState = null,
            CancellationToken cancellationToken = default)
        {
            HttpMethod httpMethod = new HttpMethod("GET");
            Guid locationId = new Guid("01d75881-6892-4ec6-8dca-91ecfb0dc048");
            object routeValues = new { planId = planId };

            List<KeyValuePair<string, string>> queryParams = new List<KeyValuePair<string, string>>();
            queryParams.Add("jobId", jobId.ToString());

            return SendAsync<JObject>(
                httpMethod,
                locationId,
                routeValues: routeValues,
                version: new ApiResourceVersion(6.0, 1),
                queryParameters: queryParams,
                userState: userState,
                cancellationToken: cancellationToken);
        }

        public Task<JObject> GetLiveAsync(
            string runId,
            object userState = null,
            CancellationToken cancellationToken = default)
        {
            HttpMethod httpMethod = new HttpMethod("GET");
            Guid locationId = new Guid("c41b3775-6d50-48bd-b261-42da7f0f1ba0");
            object routeValues = new { pipelineId = 1, runId = runId };

            List<KeyValuePair<string, string>> queryParams = new List<KeyValuePair<string, string>>();

            return SendAsync<JObject>(
                httpMethod,
                locationId,
                routeValues: routeValues,
                version: new ApiResourceVersion(6.0, 1),
                queryParameters: queryParams,
                userState: userState,
                cancellationToken: cancellationToken);
        }

        public Task<JObject> GetRunAsync(
            string runId,
            object userState = null,
            CancellationToken cancellationToken = default)
        {
            HttpMethod httpMethod = new HttpMethod("GET");
            Guid locationId = new Guid("7859261e-d2e9-4a68-b820-a5d84cc5bb3d");
            object routeValues = new { pipelineId = 1, runId = runId };

            List<KeyValuePair<string, string>> queryParams = new List<KeyValuePair<string, string>>();

            return SendAsync<JObject>(
                httpMethod,
                locationId,
                routeValues: routeValues,
                version: new ApiResourceVersion(6.0, 1),
                queryParameters: queryParams,
                userState: userState,
                cancellationToken: cancellationToken);
        }
    }
}
