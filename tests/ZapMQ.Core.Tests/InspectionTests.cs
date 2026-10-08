using Microsoft.Extensions.Time.Testing;
using ZapMQ.Core;

namespace ZapMQ.Core.Tests;

/// <summary>
/// What the administration needs from the broker: looking into a queue, pausing it, emptying
/// it, and knowing who uses it.
/// </summary>
public class InspectionTests
{
    private sealed class TestConsumer(string name) : Consumer
    {
        public List<BrokerMessage> Delivered { get; } = [];

        public override string Description => name;

        protected override void Deliver(BrokerMessage message) => Delivered.Add(message);

        protected override void DeliverResponse(BrokerMessage message) { }
    }

    private readonly FakeTimeProvider _time = new();
    private readonly Broker _broker;

    public InspectionTests() => _broker = new Broker(new BrokerOptions(), _time);

    [Fact]
    public void Pending_messages_can_be_read_without_consuming_them()
    {
        var first = _broker.Publish("orders", "{\"n\":1}");
        _time.Advance(TimeSpan.FromSeconds(5));
        var second = _broker.Publish("orders", "{\"n\":2}", rpc: true, ttl: TimeSpan.FromSeconds(30));
        _broker.Publish("orders", "{\"n\":3}");

        var pending = _broker.Peek("orders", limit: 2);

        Assert.Equal([first, second], pending.Select(message => message.Id));
        Assert.Equal("{\"n\":1}", pending[0].Body);
        Assert.Null(pending[0].Ttl);
        Assert.True(pending[1].Rpc);
        Assert.Equal(TimeSpan.FromSeconds(30), pending[1].Ttl);
        Assert.Equal(TimeSpan.FromSeconds(5), pending[1].PublishedAt - pending[0].PublishedAt);
        Assert.Equal(3, _broker.GetQueue("orders")!.Pending);
        Assert.Equal(first, _broker.Take("orders")!.Id);
        Assert.Empty(_broker.Peek("nowhere"));
    }

    [Fact]
    public void A_paused_queue_keeps_receiving_and_hands_nothing_to_anybody()
    {
        var consumer = new TestConsumer("a");
        _broker.Bind(consumer, "orders");
        _broker.SetPaused("orders", true);

        _broker.Publish("orders", "1");
        _broker.Publish("orders", "2");

        Assert.Empty(consumer.Delivered);
        Assert.Null(_broker.Take("orders"));
        var paused = _broker.GetQueue("orders")!;
        Assert.True(paused.Paused);
        Assert.Equal(2, paused.Pending);

        _broker.SetPaused("orders", false);

        Assert.Equal("1", Assert.Single(consumer.Delivered).Body);
        Assert.False(_broker.GetQueue("orders")!.Paused);
    }

    [Fact]
    public void Pausing_keeps_the_other_settings_of_the_queue()
    {
        _broker.ConfigureQueue("orders", new QueueOptions { Retention = TimeSpan.FromHours(1), RedeliverUnconfirmed = true });

        _broker.SetPaused("orders", true);

        Assert.Equal(new QueueOptions { Retention = TimeSpan.FromHours(1), RedeliverUnconfirmed = true, Paused = true }, _broker.GetQueueOptions("orders"));
        Assert.Equal(["orders"], _broker.GetAllQueueOptions().Keys);
    }

    [Fact]
    public void Emptying_a_queue_throws_the_pending_messages_away_without_making_dead_letters()
    {
        var consumer = new TestConsumer("a");
        _broker.Bind(consumer, "orders");
        _broker.Publish("orders", "being processed");
        _broker.Publish("orders", "2");
        _broker.Publish("orders", "3");

        Assert.Equal(2, _broker.Purge("orders"));

        var queue = _broker.GetQueue("orders")!;
        Assert.Equal(0, queue.Pending);
        Assert.Equal(1, queue.Processing);
        Assert.Equal(2, queue.Purged);
        Assert.Empty(_broker.GetDeadLetters("orders"));
        Assert.Equal(0, _broker.Purge("nowhere"));
    }

    [Fact]
    public void The_broker_knows_who_has_been_using_each_queue_lately()
    {
        var consumer = new TestConsumer("billing");
        _broker.Bind(consumer, "orders");
        _broker.Publish("orders", "1", publisher: "shop");
        _broker.Publish("orders", "2", publisher: "shop");
        _broker.Publish("orders", "3", publisher: "old");
        _broker.Take("orders", asker: "v1 10.0.0.9");
        _time.Advance(TimeSpan.FromMinutes(20));
        _broker.Publish("orders", "4", publisher: "shop");

        var activity = Assert.Single(_broker.GetActivity(TimeSpan.FromMinutes(10)));

        Assert.Equal("orders", activity.Queue);
        var publisher = Assert.Single(activity.Publishers);
        Assert.Equal("shop", publisher.Name);
        Assert.Equal(3, publisher.Count);
        Assert.Empty(activity.Askers);
        Assert.Equal(["billing"], activity.Consumers);
    }

    [Fact]
    public void The_totals_keep_counting_after_a_queue_is_let_go()
    {
        var consumer = new TestConsumer("a");
        _broker.Bind(consumer, "pushed");
        var id = _broker.Publish("pushed", "1");
        _broker.Confirm(consumer, "pushed", id);
        _broker.Publish("asked", "2");
        _broker.Take("asked");
        _broker.Publish("forgotten", "3", ttl: TimeSpan.FromSeconds(1));
        _broker.Unbind(consumer, "pushed");

        _time.Advance(TimeSpan.FromMinutes(10));
        _broker.Sweep();
        _time.Advance(TimeSpan.FromMinutes(10));
        _broker.Sweep();

        Assert.Empty(_broker.GetQueues());
        Assert.Equal(new BrokerTotals(Published: 3, Delivered: 2, Confirmed: 1, DeadLettered: 1), _broker.GetTotals());
    }
}
