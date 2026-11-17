using System.Text.Json;
using SystemAutorun.Core.Ipc;
using SystemAutorun.Core.Models;
using SystemAutorun.Core.Monitoring;

namespace SystemAutorun.Core.Tests;

public sealed class HostNetworkRateTrackerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-12T00:00:00Z");
    private static NetworkStatusSnapshot Nic(string id, long received, long sent) => new(id, "fixture address", received, sent, id);

    [Fact]
    public void ActualTimestampDeltasAndBothDirectionsAreAggregatedWithoutIntegerRounding()
    {
        var tracker = new HostNetworkRateTracker();
        var first = tracker.Observe(Now, [Nic("a", 1000, 500), Nic("b", 2000, 1500)]);
        Assert.Null(first.ReceiveBytesPerSecond); Assert.Null(first.SendBytesPerSecond);
        var sample = tracker.Observe(Now.AddSeconds(2.5), [Nic("b", 2300, 1750), Nic("a", 1100, 750)]);
        Assert.Equal(160, sample.ReceiveBytesPerSecond); Assert.Equal(200, sample.SendBytesPerSecond); Assert.Null(sample.Error);
    }

    [Fact]
    public void RealUnchangedCountersAreZeroButDuplicateDeliveryIsNotAnotherObservation()
    {
        var tracker = new HostNetworkRateTracker();
        tracker.Observe(Now, [Nic("a", 10, 20)]);
        Assert.Null(tracker.Observe(Now, [Nic("a", 999, 999)]).ReceiveBytesPerSecond);
        var zero = tracker.Observe(Now.AddSeconds(1), [Nic("a", 10, 20)]);
        Assert.Equal(0, zero.ReceiveBytesPerSecond); Assert.Equal(0, zero.SendBytesPerSecond);
    }

    [Theory]
    [InlineData("add")] [InlineData("remove")] [InlineData("replace")]
    public void ActiveInterfaceSetChangesCreateOneGapThenUseOnlyTheNewBaseline(string change)
    {
        var tracker = new HostNetworkRateTracker();
        tracker.Observe(Now, [Nic("a", 100, 100), Nic("b", 200, 200)]);
        NetworkStatusSnapshot[] current = change switch
        {
            "add" => [Nic("a", 200, 200), Nic("b", 400, 400), Nic("c", 5000, 5000)],
            "remove" => [Nic("a", 200, 200)],
            _ => [Nic("a", 200, 200), Nic("c", 5000, 5000)],
        };
        var gap = tracker.Observe(Now.AddSeconds(1), current);
        Assert.Null(gap.ReceiveBytesPerSecond); Assert.Null(gap.SendBytesPerSecond); Assert.Equal("network-interface-changed", gap.Error);
        var next = tracker.Observe(Now.AddSeconds(2), current.Select(row => row with { BytesReceived = row.BytesReceived + 10 }).ToArray());
        Assert.Equal(current.Length * 10, next.ReceiveBytesPerSecond); Assert.Equal(0, next.SendBytesPerSecond);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void EitherCounterResetIsUnknownInsteadOfClampedToZero(bool receive)
    {
        var tracker = new HostNetworkRateTracker();
        tracker.Observe(Now, [Nic("a", 100, 100)]);
        var gap = tracker.Observe(Now.AddSeconds(1), [Nic("a", receive ? 10 : 200, receive ? 200 : 10)]);
        Assert.Equal("network-counter-reset", gap.Error); Assert.Null(gap.ReceiveBytesPerSecond); Assert.Null(gap.SendBytesPerSecond);
        var next = tracker.Observe(Now.AddSeconds(2), [Nic("a", receive ? 20 : 210, receive ? 210 : 20)]);
        Assert.Equal(10, next.ReceiveBytesPerSecond); Assert.Equal(10, next.SendBytesPerSecond);
    }

    [Theory]
    [InlineData(-1)] [InlineData(4)]
    public void ClockRegressionOrLongSilenceDoesNotProduceASpanningAverage(int seconds)
    {
        var tracker = new HostNetworkRateTracker();
        tracker.Observe(Now, [Nic("a", 100, 100)]);
        var gap = tracker.Observe(Now.AddSeconds(seconds), [Nic("a", 500, 500)]);
        Assert.Equal("network-sample-gap", gap.Error); Assert.Null(gap.ReceiveBytesPerSecond);
        Assert.Equal(100, tracker.Observe(Now.AddSeconds(seconds + 1), [Nic("a", 600, 600)]).ReceiveBytesPerSecond);
    }

    [Fact]
    public void LabelsCanChangeWithoutReidentifyingAnInterface()
    {
        var tracker = new HostNetworkRateTracker();
        tracker.Observe(Now, [Nic("stable-id", 100, 100)]);
        var sample = tracker.Observe(Now.AddSeconds(1), [Nic("stable-id", 200, 300) with { Name = "renamed", Address = "changed" }]);
        Assert.Equal(100, sample.ReceiveBytesPerSecond); Assert.Equal(200, sample.SendBytesPerSecond);
    }

    [Theory]
    [InlineData("empty")] [InlineData("legacy")] [InlineData("duplicate")] [InlineData("negative")]
    public void UnidentifiableOrInvalidCountersDiscardTheBaseline(string kind)
    {
        var tracker = new HostNetworkRateTracker(); tracker.Observe(Now, [Nic("a", 100, 100)]);
        NetworkStatusSnapshot[] rows = kind switch
        {
            "empty" => [],
            "legacy" => [new("name is not identity", "same address", 100, 100)],
            "duplicate" => [Nic("a", 100, 100), Nic("a", 100, 100)],
            _ => [Nic("a", -1, 100)],
        };
        var gap = tracker.Observe(Now.AddSeconds(1), rows);
        Assert.Equal("network-identity-unavailable", gap.Error); Assert.Null(gap.ReceiveBytesPerSecond);
        Assert.Equal("network-warming-up", tracker.Observe(Now.AddSeconds(2), [Nic("a", 200, 200)]).Error);
    }

    [Fact]
    public void ExplicitReadFailureResetRequiresAnotherRealBaseline()
    {
        var tracker = new HostNetworkRateTracker(); tracker.Observe(Now, [Nic("a", 100, 100)]); tracker.Reset();
        Assert.Equal("network-warming-up", tracker.Observe(Now.AddSeconds(1), [Nic("a", 200, 200)]).Error);
    }

    [Fact]
    public void LegacyHostJsonDoesNotInventRatesOrInterfaceIdentity()
    {
        const string json = """{"machineName":"fixture","osDescription":"fixture","capturedAt":"2026-11-12T00:00:00Z","uptime":"01:00:00","cpuUsagePercent":1,"totalMemoryBytes":1000,"availableMemoryBytes":500,"drives":[],"networks":[{"name":"old","address":"fixture","bytesReceived":100,"bytesSent":200}]}""";
        var host = JsonSerializer.Deserialize<HostStatusSnapshot>(json, AgentProtocol.JsonOptions)!;
        Assert.Null(host.DiskReadBytesPerSecond); Assert.Null(host.DiskWriteBytesPerSecond);
        Assert.Null(host.NetworkReceiveBytesPerSecond); Assert.Null(host.NetworkSendBytesPerSecond);
        Assert.Null(Assert.Single(host.Networks).InterfaceId);
    }
}
