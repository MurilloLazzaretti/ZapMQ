using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using ZapMQ.Core;

namespace ZapMQ.Core.Tests;

public class BrokerTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly Broker _broker;

    public BrokerTests() => _broker = new Broker(new BrokerOptions(), _time);

    [Fact]
    public void Take_from_a_queue_that_never_existed_returns_nothing()
    {
        Assert.Null(_broker.Take("orders"));
    }

    [Fact]
    public void Messages_are_delivered_in_publication_order()
    {
        _broker.Publish("orders", "1");
        _broker.Publish("orders", "2");
        _broker.Publish("orders", "3");

        Assert.Equal("1", _broker.Take("orders")!.Body);
        Assert.Equal("2", _broker.Take("orders")!.Body);
        Assert.Equal("3", _broker.Take("orders")!.Body);
        Assert.Null(_broker.Take("orders"));
    }

    [Fact]
    public void Delivered_message_carries_what_was_published()
    {
        var id = _broker.Publish("orders", "{\"a\":1}", rpc: true);

        var message = _broker.Take("orders")!;

        Assert.Equal(id, message.Id);
        Assert.Equal("orders", message.Queue);
        Assert.Equal("{\"a\":1}", message.Body);
        Assert.True(message.Rpc);
        Assert.Null(message.Response);
    }

    [Fact]
    public void Message_id_keeps_the_1x_format()
    {
        var id = _broker.Publish("orders", "{}");

        Assert.Matches("^\\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\\}$", id);
    }

    [Fact]
    public void Queue_names_are_case_sensitive()
    {
        _broker.Publish("Orders", "{}");

        Assert.Null(_broker.Take("orders"));
        Assert.NotNull(_broker.Take("Orders"));
    }

    [Fact]
    public async Task Concurrent_consumers_never_receive_the_same_message()
    {
        const int total = 20_000;
        var broker = new Broker();
        for (var i = 0; i < total; i++)
            broker.Publish("orders", i.ToString());

        var received = new ConcurrentBag<string>();
        var consumers = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            while (broker.Take("orders") is { } message)
                received.Add(message.Id);
        }));
        await Task.WhenAll(consumers);

        Assert.Equal(total, received.Count);
        Assert.Equal(total, received.Distinct().Count());
    }

    [Fact]
    public async Task Publishing_while_consuming_loses_and_duplicates_nothing()
    {
        const int perPublisher = 5_000;
        const int publishers = 4;
        var broker = new Broker();
        var received = new ConcurrentBag<string>();
        var publishing = true;

        var consumers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            while (true)
            {
                if (broker.Take("orders") is { } message)
                    received.Add(message.Body);
                else if (!Volatile.Read(ref publishing))
                    break;
            }
        })).ToArray();

        await Task.WhenAll(Enumerable.Range(0, publishers).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < perPublisher; i++)
                broker.Publish("orders", $"{p}:{i}");
        })));
        Volatile.Write(ref publishing, false);
        await Task.WhenAll(consumers);
        while (broker.Take("orders") is { } rest)
            received.Add(rest.Body);

        Assert.Equal(perPublisher * publishers, received.Count);
        Assert.Equal(perPublisher * publishers, received.Distinct().Count());
    }

    [Fact]
    public void Message_past_its_ttl_is_not_delivered()
    {
        _broker.Publish("orders", "short", ttl: TimeSpan.FromSeconds(5));
        _broker.Publish("orders", "long", ttl: TimeSpan.FromSeconds(60));

        _time.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal("long", _broker.Take("orders")!.Body);
        Assert.Null(_broker.Take("orders"));
        Assert.Equal(1, _broker.GetQueues().Single().Expired);
    }

    [Fact]
    public void Message_exactly_at_its_ttl_is_still_delivered()
    {
        _broker.Publish("orders", "x", ttl: TimeSpan.FromSeconds(5));

        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.NotNull(_broker.Take("orders"));
    }

    [Fact]
    public void Message_without_ttl_lasts_until_the_retention()
    {
        _broker.Publish("orders", "x");

        _time.Advance(TimeSpan.FromSeconds(180));
        _broker.Sweep();
        Assert.Equal(1, _broker.GetQueues().Single().Pending);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(_broker.Take("orders"));
        Assert.Equal(1, _broker.GetQueues().Single().Dropped);
    }

    [Fact]
    public void Sweep_discards_expired_and_over_retention_messages()
    {
        _broker.Publish("orders", "expires", ttl: TimeSpan.FromSeconds(5));
        _broker.Publish("orders", "stays");

        _time.Advance(TimeSpan.FromSeconds(10));
        _broker.Sweep();

        var queue = _broker.GetQueues().Single();
        Assert.Equal(1, queue.Pending);
        Assert.Equal(1, queue.Expired);
        Assert.Equal(0, queue.Dropped);
    }

    [Fact]
    public void Retention_is_configurable()
    {
        var broker = new Broker(new BrokerOptions { Retention = TimeSpan.FromHours(1) }, _time);
        broker.Publish("orders", "x");

        _time.Advance(TimeSpan.FromMinutes(59));

        Assert.NotNull(broker.Take("orders"));
    }

    [Fact]
    public void Rpc_response_reaches_the_sender_exactly_once()
    {
        var id = _broker.Publish("orders", "ask", rpc: true);

        Assert.Null(_broker.TakeResponse("orders", id));

        _broker.Take("orders");
        Assert.Null(_broker.TakeResponse("orders", id));

        Assert.True(_broker.Respond("orders", id, "answer"));

        var answered = _broker.TakeResponse("orders", id)!;
        Assert.Equal("ask", answered.Body);
        Assert.Equal("answer", answered.Response);
        Assert.Null(_broker.TakeResponse("orders", id));
    }

    [Fact]
    public void Rpc_message_is_delivered_to_a_single_consumer()
    {
        _broker.Publish("orders", "ask", rpc: true);

        Assert.NotNull(_broker.Take("orders"));
        Assert.Null(_broker.Take("orders"));
    }

    [Fact]
    public void Respond_is_refused_for_a_message_that_is_no_longer_in_the_queue()
    {
        var plain = _broker.Publish("plain", "tell");
        _broker.Take("plain");
        Assert.False(_broker.Respond("plain", plain, "answer"));

        Assert.False(_broker.Respond("plain", "{UNKNOWN}", "answer"));
        Assert.False(_broker.Respond("missing", plain, "answer"));
    }

    [Fact]
    public void Undelivered_message_cannot_be_answered_by_default()
    {
        var id = _broker.Publish("orders", "ask", rpc: true);

        Assert.False(_broker.Respond("orders", id, "answer"));
        Assert.Equal("ask", _broker.Take("orders")!.Body);
    }

    [Fact]
    public void Message_answered_before_delivery_is_never_delivered()
    {
        _broker.Publish("orders", "first");
        var id = _broker.Publish("orders", "ask", rpc: true);
        _broker.Publish("orders", "last");

        Assert.True(_broker.Contains("orders", id));
        Assert.True(_broker.Respond("orders", id, "answer", includeUndelivered: true));

        Assert.Equal("first", _broker.Take("orders")!.Body);
        Assert.Equal("last", _broker.Take("orders")!.Body);
        Assert.Null(_broker.Take("orders"));
        Assert.Equal("answer", _broker.TakeResponse("orders", id)!.Response);
        Assert.False(_broker.Contains("orders", id));
    }

    [Fact]
    public void Rpc_waiting_for_a_response_is_dropped_after_the_retention()
    {
        var id = _broker.Publish("orders", "ask", rpc: true);
        _broker.Take("orders");

        _time.Advance(TimeSpan.FromSeconds(181));

        Assert.False(_broker.Respond("orders", id, "late"));
        _broker.Sweep();
        Assert.Equal(1, _broker.GetQueues().Single().Dropped);
    }

    [Fact]
    public void Uncollected_rpc_response_is_dropped_after_the_retention()
    {
        var id = _broker.Publish("orders", "ask", rpc: true);
        _broker.Take("orders");
        _broker.Respond("orders", id, "answer");

        _time.Advance(TimeSpan.FromSeconds(181));
        _broker.Sweep();

        Assert.Null(_broker.TakeResponse("orders", id));
    }

    [Fact]
    public void Empty_queue_is_removed_only_after_its_lifetime()
    {
        _broker.Publish("orders", "x");
        _broker.Take("orders");

        _time.Advance(TimeSpan.FromSeconds(60));
        _broker.Sweep();
        Assert.Single(_broker.GetQueues());

        _time.Advance(TimeSpan.FromSeconds(1));
        _broker.Sweep();
        Assert.Empty(_broker.GetQueues());
    }

    [Fact]
    public void Queue_with_messages_is_never_removed()
    {
        var broker = new Broker(new BrokerOptions { Retention = TimeSpan.FromDays(1) }, _time);
        broker.Publish("orders", "x");

        _time.Advance(TimeSpan.FromHours(1));
        broker.Sweep();

        Assert.Single(broker.GetQueues());
        Assert.NotNull(broker.Take("orders"));
    }

    [Fact]
    public void Publishing_again_cancels_the_pending_removal()
    {
        _broker.Publish("orders", "x");
        _broker.Take("orders");
        _time.Advance(TimeSpan.FromSeconds(50));

        _broker.Publish("orders", "y");
        _broker.Take("orders");
        _time.Advance(TimeSpan.FromSeconds(50));
        _broker.Sweep();

        Assert.Single(_broker.GetQueues());
    }

    [Fact]
    public void Removed_queue_is_recreated_on_the_next_publish()
    {
        _broker.Publish("orders", "x");
        _broker.Take("orders");
        _time.Advance(TimeSpan.FromMinutes(2));
        _broker.Sweep();

        _broker.Publish("orders", "y");

        Assert.Equal("y", _broker.Take("orders")!.Body);
    }

    [Fact]
    public async Task Publishing_while_queues_are_being_removed_loses_nothing()
    {
        var broker = new Broker(new BrokerOptions { EmptyQueueLifetime = TimeSpan.Zero });
        var sweeping = true;
        var sweeper = Task.Run(() =>
        {
            while (Volatile.Read(ref sweeping))
                broker.Sweep();
        });

        const int total = 20_000;
        var received = 0;
        for (var i = 0; i < total; i++)
        {
            broker.Publish("orders", i.ToString());
            if (broker.Take("orders") is not null)
                received++;
        }

        Volatile.Write(ref sweeping, false);
        await sweeper;

        Assert.Equal(total, received);
    }

    [Fact]
    public void Counters_follow_the_message_flow()
    {
        var id = _broker.Publish("orders", "ask", rpc: true);
        _broker.Publish("orders", "tell");

        var queue = _broker.GetQueues().Single();
        Assert.Equal((2, 0, 0, 2L, 0L), (queue.Pending, queue.AwaitingResponse, queue.Answered, queue.Published, queue.Delivered));

        _broker.Take("orders");
        _broker.Take("orders");
        queue = _broker.GetQueues().Single();
        Assert.Equal((0, 1, 0, 2L), (queue.Pending, queue.AwaitingResponse, queue.Answered, queue.Delivered));

        _broker.Respond("orders", id, "answer");
        queue = _broker.GetQueues().Single();
        Assert.Equal((0, 1, 1L), (queue.AwaitingResponse, queue.Answered, queue.Responded));
    }

    [Fact]
    public void Publish_rejects_an_unnamed_queue()
    {
        Assert.Throws<ArgumentException>(() => _broker.Publish("", "{}"));
    }
}
