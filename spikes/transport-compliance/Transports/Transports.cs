using Bobcat;
using Bobcat.Xunit;
using Compliance;
using Xunit;

namespace Transports.Tcp
{
    public class LightweightTcpFixture : TransportComplianceFixture
    {
        public override FakeTransport BuildTransport() => new() { Scheme = "tcp" };
    }

    // Exactly Wolverine's shape: bodyless.
    [BobcatFeature("Lightweight TCP transport compliance")]
    public class LightweightTcpTransportCompliance : TransportCompliance<LightweightTcpFixture>;

    public class DurableTcpTransportFixture : TransportComplianceFixture
    {
        public override FakeTransport BuildTransport() => new() { Scheme = "tcp", SupportsScheduledRetry = false };
    }

    // Bodyless too -- and this fake transport cannot schedule a retry, so can_schedule_retry goes red here only.
    [BobcatFeature("Durable TCP transport compliance")]
    public class DurableTcpTransportCompliance : TransportCompliance<DurableTcpTransportFixture>;
}

namespace Transports.Sqlite
{
    public class LocalSqliteBackedFixture : TransportComplianceFixture
    {
        public override FakeTransport BuildTransport() => new() { Scheme = "sqlite", IsSenderOnly = true };
    }

    // LocalSqliteBackedTransportCompliance's shape: one override skipped, one override re-specified.
    // Wolverine skips with [Fact(Skip = …)]. Once the base method is a [BobcatSpec], that override errors
    // at run time ("has multiple [Fact]-derived attributes": the inherited [BobcatSpec] plus the new
    // [Fact]); [BobcatSpec(Skip = …)] replaces the inherited attribute and skips cleanly.
    public class LocalSqliteBackedTransportCompliance : TransportCompliance<LocalSqliteBackedFixture>
    {
        [BobcatSpec(Skip = "Not meaningful for SQLite local transport: there is no second node.")]
        public override void will_move_to_dead_letter_queue_with_exception_match() { }

        [BobcatSpec]
        public override void can_schedule_retry()
        {
            the.HandlerThrowsOnAttempt(nameof(BadImageFormatException), 1);
            the.AMessageIsSent();
            the.MessageEnds("succeeded", 2);
        }
    }
}

namespace Transports.Kafka
{
    public class BufferedComplianceFixture : TransportComplianceFixture
    {
        public override FakeTransport BuildTransport() => new() { Scheme = "kafka" };
    }

    // Wolverine names a class BufferedSendingAndReceivingCompliance in SEVEN transport projects (SQS, SNS,
    // GCP, Kafka, Azure Service Bus, …); two namespaces in one assembly stand in for that here.
    public class BufferedSendingAndReceivingCompliance : TransportCompliance<BufferedComplianceFixture>;
}

namespace Transports.Sqs
{
    public class BufferedComplianceFixture : TransportComplianceFixture
    {
        public override FakeTransport BuildTransport() => new() { Scheme = "sqs" };
    }

    public class BufferedSendingAndReceivingCompliance : TransportCompliance<BufferedComplianceFixture>;
}
