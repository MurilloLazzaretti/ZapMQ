using Microsoft.Extensions.Time.Testing;
using ZapMQ.Core;

namespace ZapMQ.Core.Tests;

/// <summary>
/// Queues for what is only worth having while somebody is taking it.
/// </summary>
public class DisposableQueueTests
{
    private sealed class TestConsumer : Consumer
    {
        public List<BrokerMessage> Delivered { get; } = [];

        public override string Description => "test";

        protected override void Deliver(BrokerMessage message) => Delivered.Add(message);

        protected override void DeliverResponse(BrokerMessage message) { }
    }

    private readonly FakeTimeProvider _time = new();
    private readonly Broker _broker;

    public DisposableQueueTests()
    {
        var options = new BrokerOptions { Retention = TimeSpan.FromMinutes(3), DisposableRetention = TimeSpan.FromSeconds(15) };
        options.DisposablePrefixes.Add("live.");
        _broker = new Broker(options, _time);
    }

    [Fact]
    public void What_nobody_takes_is_thrown_away_shortly_and_leaves_no_dead_letter()
    {
        _broker.Publish("live.one", "{}");
        _broker.Publish("work", "{}");

        _time.Advance(TimeSpan.FromSeconds(20));
        _broker.Sweep();

        Assert.Equal(0, _broker.GetQueue("live.one")?.Pending ?? 0);
        Assert.Equal(1, _broker.GetQueue("work")!.Pending);
        Assert.Empty(_broker.GetDeadLetters("live.one"));
    }

    [Fact]
    public void A_message_its_consumer_never_confirmed_leaves_no_dead_letter_either()
    {
        var consumer = new TestConsumer();
        _broker.Bind(consumer, "live.one");
        _broker.Publish("live.one", "{}");
        Assert.Single(consumer.Delivered);

        _broker.Disconnect(consumer);

        Assert.Empty(_broker.GetDeadLetters("live.one"));
        Assert.Empty(_broker.GetDeadLetterSummary());
    }

    [Fact]
    public void Settings_given_to_such_a_queue_do_not_change_what_it_is()
    {
        _broker.ConfigureQueue("live.one", new QueueOptions { DeadLetterLimit = 50, Retention = TimeSpan.FromHours(1) });
        _broker.Publish("live.one", "{}");

        _time.Advance(TimeSpan.FromSeconds(20));
        _broker.Sweep();

        Assert.Empty(_broker.GetDeadLetters("live.one"));
    }
}
