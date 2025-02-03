using System.Collections.Generic;
using System.Runtime.Serialization;
using GitHub.DistributedTask.WebApi;

namespace GitHub.Actions.RunService.WebApi
{
    [DataContract]
    public class AcquireJobRequest
    {
        [DataMember(Name = "streamId", EmitDefaultValue = false)]
        public string StreamID { get; set; }

        [DataMember(Name = "jobMessageId", EmitDefaultValue = false)]
        public string JobMessageId { get; set; }

        [DataMember(Name = "runnerOS", EmitDefaultValue = false)]
        public string RunnerOS { get; set; }

        [DataMember(Name = "billingOwnerId", EmitDefaultValue = false)]
        public string BillingOwnerId { get; set; }
    }
}
