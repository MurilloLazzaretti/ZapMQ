using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using ZapMQ.Core;

namespace ZapMQ.Core.Tests;

public class PushTests
{
    private sealed class TestConsumer(string name) : Consumer
    {
        public List<BrokerMessage> Delivered { get; } = [];

        public List<BrokerMessage> Responses { get; } = [];

        public override string Description => name;

        public BrokerMessage Last => Delivered[^1];

        public string[] Bodies => Delivered.Select(m => m.Body).ToArray();

        protected override void Deliver(BrokerMessage message) => Delivered.Add(message);

        protected override void DeliverResponse(BrokerMessage message) => Responses.Add(message);
    }

    private readonly FakeTimeProvider _time = new();
    private readonly Broker _broker;

    public PushTests() => _broker = new Broker(new BrokerOptions(), _time);

    private TestConsumer Bound(string name, params string[] queues)
    {
        var consumer = new TestConsumer(name);
        foreach (var queue in queues)
            _broker.Bind(consumer, queue);
        return consumer;
    }

    [Fact]
    public void Bound_consumer_receives_a_message_as_soon_as_it_is_published()
    {
        var consumer = Bound("a", "orders");

        var id = _broker.Publish("orders", "1");

        Assert.Equal(id, consumer.Delivered.Single().Id);
        Assert.Equal(1, _broker.GetQueues().Single().Processing);
    }

    [Fact]
    public void Messages_already_waiting_are_pushed_on_bind()
    {
        _broker.Publish("orders", "1");

        var consumer = Bound("a", "orders");

        Assert.Equal(["1"], consumer.Bodies);
    }

    [Fact]
    public void Consumer_gets_the_next_message_only_after_confirming_the_current_one()
    {
        var consumer = Bound("a", "orders");
        _broker.Publish("orders", "1");
        _broker.Publish("orders", "2");

        Assert.Equal(["1"], consumer.Bodies);

        Assert.True(_broker.Confirm(consumer, "orders", consumer.Last.Id));

        Assert.Equal(["1", "2"], consumer.Bodies);
    }

    [Fact]
    public void Consumer_holds_one_message_at_a_time_across_all_its_queues()
    {
        var consumer = Bound("a", "orders", "invoices");
        _broker.Publish("orders", "o1");
        _broker.Publish("invoices", "i1");

        Assert.Equal(["o1"], consumer.Bodies);

        _broker.Confirm(consumer, "orders", consumer.Last.Id);

        Assert.Equal(["o1", "i1"], consumer.Bodies);
    }

    [Fact]
    public void Queues_of_a_consumer_are_served_in_turn()
    {
        _broker.Publish("orders", "o1");
        _broker.Publish("orders", "o2");
        _broker.Publish("orders", "o3");
        _broker.Publish("invoices", "i1");
        var consumer = Bound("a", "orders", "invoices");

        while (consumer.Delivered.Count < 4)
            _broker.Confirm(consumer, consumer.Last.Queue, consumer.Last.Id);

        Assert.Equal(["o1", "i1", "o2", "o3"], consumer.Bodies);
    }

    [Fact]
    public void Consumers_of_a_queue_are_served_in_turn()
    {
        var first = Bound("a", "orders");
        var second = Bound("b", "orders");

        _broker.Publish("orders", "1");
        _broker.Publish("orders", "2");

        Assert.Equal(["1"], first.Bodies);
        Assert.Equal(["2"], second.Bodies);
    }

    [Fact]
    public void Message_waits_when_every_consumer_is_busy_and_goes_to_the_first_one_freed()
    {
        var first = Bound("a", "orders");
        var second = Bound("b", "orders");
        _broker.Publish("orders", "1");
        _broker.Publish("orders", "2");
        _broker.Publish("orders", "3");

        Assert.Equal(1, _broker.GetQueues().Single().Pending);

        _broker.Confirm(second, "orders", second.Last.Id);

        Assert.Equal(["2", "3"], second.Bodies);
        Assert.Equal(["1"], first.Bodies);
    }

    [Fact]
    public void Only_the_consumer_holding_a_message_can_confirm_it()
    {
        var holder = Bound("a", "orders");
        var other = new TestConsumer("b");
        var id = _broker.Publish("orders", "1");

        Assert.False(_broker.Confirm(other, "orders", id));
        Assert.False(_broker.Confirm(holder, "orders", "{UNKNOWN}"));
        Assert.False(_broker.Confirm(holder, "missing", id));
        Assert.True(_broker.Confirm(holder, "orders", id));
        Assert.False(_broker.Confirm(holder, "orders", id));
    }

    [Fact]
    public void Pushed_message_is_not_available_to_a_consumer_that_asks()
    {
        Bound("a", "orders");
        _broker.Publish("orders", "1");

        Assert.Null(_broker.Take("orders"));
    }

    [Fact]
    public void Unconfirmed_message_of_a_lost_consumer_becomes_a_dead_letter_and_is_not_redelivered()
    {
        var lost = Bound("worker-1", "orders");
        var other = Bound("worker-2", "orders");
        var id = _broker.Publish("orders", "1");

        _broker.Disconnect(lost);

        Assert.Empty(other.Delivered);
        Assert.Null(_broker.Take("orders"));
        var letter = _broker.GetDeadLetters("orders").Single();
        Assert.Equal((id, "1", DeadLetterReason.Unconfirmed, "worker-1"), (letter.Id, letter.Body, letter.Reason, letter.Consumer));
        Assert.Equal(1, _broker.GetQueues().Single().Unconfirmed);
    }

    [Fact]
    public void Queue_set_to_redeliver_hands_the_unconfirmed_message_to_another_consumer_first()
    {
        _broker.ConfigureQueue("orders", new QueueOptions { RedeliverUnconfirmed = true });
        var lost = Bound("a", "orders");
        _broker.Publish("orders", "1");
        _broker.Publish("orders", "2");
        var other = Bound("b", "orders");
        // "b" took "2" on binding; free it so that the order of what comes next is visible.
        _broker.Publish("orders", "3");

        _broker.Disconnect(lost);
        _broker.Confirm(other, "orders", other.Last.Id);

        Assert.Equal(["2", "1"], other.Bodies);
        Assert.Empty(_broker.GetDeadLetters("orders"));
        Assert.Equal(1, _broker.GetQueues().Single().Redelivered);
    }

    [Fact]
    public void Redelivery_can_be_switched_on_while_the_queue_is_in_use()
    {
        var lost = Bound("a", "orders");
        _broker.Publish("orders", "1");

        _broker.ConfigureQueue("orders", new QueueOptions { RedeliverUnconfirmed = true });
        _broker.Disconnect(lost);

        Assert.Equal("1", _broker.Take("orders")!.Body);
        Assert.True(_broker.GetQueueOptions("orders")!.RedeliverUnconfirmed);
    }

    [Fact]
    public void Losing_an_idle_consumer_loses_nothing()
    {
        var idle = Bound("a", "orders");
        _broker.Disconnect(idle);

        _broker.Publish("orders", "1");

        Assert.Empty(idle.Delivered);
        Assert.Empty(_broker.GetDeadLetters("orders"));
        Assert.Equal("1", _broker.Take("orders")!.Body);
    }

    [Fact]
    public void Unbinding_stops_new_messages_but_keeps_the_one_being_processed()
    {
        var consumer = Bound("a", "orders");
        var id = _broker.Publish("orders", "1");
        _broker.Publish("orders", "2");

        _broker.Unbind(consumer, "orders");

        Assert.True(_broker.Confirm(consumer, "orders", id));
        Assert.Equal(["1"], consumer.Bodies);
        Assert.Equal("2", _broker.Take("orders")!.Body);
    }

    [Fact]
    public void Message_being_processed_has_no_deadline()
    {
        var consumer = Bound("a", "orders");
        var id = _broker.Publish("orders", "1", ttl: TimeSpan.FromSeconds(5));

        _time.Advance(TimeSpan.FromHours(1));
        _broker.Sweep();

        Assert.True(_broker.Confirm(consumer, "orders", id));
        Assert.Empty(_broker.GetDeadLetters("orders"));
    }

    [Fact]
    public void Rpc_answer_is_pushed_once_to_the_consumer_that_asked()
    {
        var asker = new TestConsumer("asker");
        var worker = Bound("worker", "orders");

        var id = _broker.Publish("orders", "ask", rpc: true, replyTo: asker);
        Assert.True(_broker.Respond(worker, "orders", id, "answer"));

        var answered = asker.Responses.Single();
        Assert.Equal((id, "ask", "answer"), (answered.Id, answered.Body, answered.Response));
        Assert.Null(_broker.TakeResponse("orders", id));
        Assert.False(worker.IsBusy);
    }

    [Fact]
    public void Rpc_asked_by_push_and_answered_the_1x_way_reaches_the_asker()
    {
        var asker = new TestConsumer("asker");
        var id = _broker.Publish("orders", "ask", rpc: true, replyTo: asker);

        _broker.Take("orders");
        Assert.True(_broker.Respond("orders", id, "answer"));

        Assert.Equal("answer", asker.Responses.Single().Response);
    }

    [Fact]
    public void Rpc_asked_the_1x_way_and_answered_by_push_is_collected_the_1x_way()
    {
        var worker = Bound("worker", "orders");
        var id = _broker.Publish("orders", "ask", rpc: true);

        Assert.True(_broker.Respond(worker, "orders", id, "answer"));

        Assert.Equal("answer", _broker.TakeResponse("orders", id)!.Response);
    }

    [Fact]
    public void Message_being_processed_by_a_pushed_consumer_cannot_be_answered_by_somebody_else()
    {
        Bound("worker", "orders");
        var id = _broker.Publish("orders", "ask", rpc: true);

        Assert.False(_broker.Respond("orders", id, "answer", includeUndelivered: true));
    }

    [Fact]
    public void Answer_that_arrives_while_the_asker_is_away_is_handed_over_when_it_asks_again()
    {
        var asker = new TestConsumer("asker");
        var worker = Bound("worker", "orders");
        var id = _broker.Publish("orders", "ask", rpc: true, replyTo: asker);

        _broker.Disconnect(asker);
        _broker.Respond(worker, "orders", id, "answer");
        Assert.Empty(asker.Responses);

        var back = new TestConsumer("asker again");
        Assert.Equal(1, _broker.Await(back, "orders", [id, "{UNKNOWN}"]));

        Assert.Equal("answer", back.Responses.Single().Response);
        Assert.Equal(0, _broker.Await(back, "orders", [id]));
    }

    [Fact]
    public void Asking_again_before_the_answer_routes_it_to_the_new_consumer()
    {
        var asker = new TestConsumer("asker");
        var worker = Bound("worker", "orders");
        var id = _broker.Publish("orders", "ask", rpc: true, replyTo: asker);
        _broker.Disconnect(asker);

        var back = new TestConsumer("asker again");
        _broker.Await(back, "orders", [id]);
        _broker.Respond(worker, "orders", id, "answer");

        Assert.Equal("answer", back.Responses.Single().Response);
    }

    [Fact]
    public void Unanswered_rpc_is_dropped_without_becoming_a_dead_letter()
    {
        var id = _broker.Publish("orders", "ask", rpc: true);
        _broker.Take("orders");

        _time.Advance(TimeSpan.FromSeconds(181));
        _broker.Sweep();

        Assert.Empty(_broker.GetDeadLetters("orders"));
        Assert.Equal(1, _broker.GetQueues().Single().Dropped);
        Assert.False(_broker.Contains("orders", id));
    }

    [Fact]
    public void Expired_and_unconsumed_messages_become_dead_letters_with_their_reason()
    {
        var expired = _broker.Publish("orders", "short", ttl: TimeSpan.FromSeconds(5));
        var stale = _broker.Publish("orders", "long");

        _time.Advance(TimeSpan.FromSeconds(181));
        _broker.Sweep();

        var letters = _broker.GetDeadLetters("orders");
        Assert.Equal(DeadLetterReason.Expired, letters.Single(l => l.Id == expired).Reason);
        Assert.Equal(DeadLetterReason.NotConsumed, letters.Single(l => l.Id == stale).Reason);
        Assert.Equal(new DeadLetterSummary("orders", 1, 1, 0), _broker.GetDeadLetterSummary().Single());
        Assert.Null(letters[0].Consumer);
    }

    [Fact]
    public void Dead_letters_outlive_their_queue_and_are_listed_newest_first()
    {
        _broker.Publish("orders", "1", ttl: TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(2));
        _broker.Sweep();
        _broker.Publish("orders", "2", ttl: TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromMinutes(5));
        _broker.Sweep();
        _time.Advance(TimeSpan.FromMinutes(2));
        _broker.Sweep();

        Assert.Empty(_broker.GetQueues());
        Assert.Equal(["2", "1"], _broker.GetDeadLetters("orders").Select(l => l.Body));
    }

    [Fact]
    public void Requeued_dead_letter_is_published_again_as_a_copy()
    {
        var id = _broker.Publish("orders", "1", ttl: TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(2));
        _broker.Sweep();

        var copy = _broker.RequeueDeadLetter("orders", id);

        var delivered = _broker.Take("orders")!;
        Assert.Equal((copy, "1", id), (delivered.Id, delivered.Body, delivered.RequeuedFrom));
        Assert.NotEqual(id, copy);
        Assert.Empty(_broker.GetDeadLetters("orders"));
        Assert.Null(_broker.RequeueDeadLetter("orders", id));
    }

    [Fact]
    public void Dead_letters_can_be_read_and_discarded()
    {
        var first = _broker.Publish("orders", "1", ttl: TimeSpan.FromSeconds(1));
        _broker.Publish("orders", "2", ttl: TimeSpan.FromSeconds(1));
        _broker.Publish("orders", "3", ttl: TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(2));
        _broker.Sweep();

        Assert.Equal("1", _broker.GetDeadLetter("orders", first)!.Body);
        Assert.True(_broker.DiscardDeadLetter("orders", first));
        Assert.False(_broker.DiscardDeadLetter("orders", first));
        Assert.Null(_broker.GetDeadLetter("orders", first));
        Assert.Equal(2, _broker.DiscardDeadLetters("orders"));
        Assert.Empty(_broker.GetDeadLetterSummary());
    }

    [Fact]
    public void Dead_letters_are_limited_in_number_and_in_age()
    {
        var broker = new Broker(new BrokerOptions { DeadLetterLimit = 2, DeadLetterMaxAge = TimeSpan.FromHours(1) }, _time);
        foreach (var body in new[] { "1", "2", "3" })
            broker.Publish("orders", body, ttl: TimeSpan.FromSeconds(1));
        _time.Advance(TimeSpan.FromSeconds(2));
        broker.Sweep();

        Assert.Equal(["3", "2"], broker.GetDeadLetters("orders").Select(l => l.Body));

        _time.Advance(TimeSpan.FromMinutes(61));
        broker.Sweep();

        Assert.Empty(broker.GetDeadLetters("orders"));
    }

    [Fact]
    public void Queue_can_keep_no_dead_letters_or_more_than_the_default()
    {
        var options = new BrokerOptions { DeadLetterLimit = 1 };
        options.Queues["noise"] = new QueueOptions { DeadLetterLimit = 0 };
        options.Queues["orders"] = new QueueOptions { DeadLetterLimit = 5, Retention = TimeSpan.FromHours(1) };
        var broker = new Broker(options, _time);
        foreach (var queue in new[] { "noise", "orders", "other" })
        {
            broker.Publish(queue, "1", ttl: TimeSpan.FromSeconds(1));
            broker.Publish(queue, "2", ttl: TimeSpan.FromSeconds(1));
        }
        broker.Publish("orders", "kept");

        _time.Advance(TimeSpan.FromMinutes(10));
        broker.Sweep();

        Assert.Empty(broker.GetDeadLetters("noise"));
        Assert.Equal(2, broker.GetDeadLetters("orders").Count);
        Assert.Single(broker.GetDeadLetters("other"));
        Assert.Equal("kept", broker.Take("orders")!.Body);
    }

    [Fact]
    public void Queue_with_a_bound_consumer_is_kept_while_empty()
    {
        var consumer = Bound("a", "orders");

        _time.Advance(TimeSpan.FromHours(1));
        _broker.Sweep();
        _broker.Publish("orders", "1");

        Assert.Equal(["1"], consumer.Bodies);

        _broker.Confirm(consumer, "orders", consumer.Last.Id);
        _broker.Disconnect(consumer);
        _time.Advance(TimeSpan.FromMinutes(2));
        _broker.Sweep();

        Assert.Empty(_broker.GetQueues());
    }

    [Fact]
    public void Closed_consumer_cannot_be_bound_again()
    {
        var consumer = Bound("a", "orders");
        _broker.Disconnect(consumer);

        _broker.Bind(consumer, "orders");
        _broker.Publish("orders", "1");

        Assert.Empty(consumer.Delivered);
    }

    /// <summary>
    /// A consumer that processes on its own thread, like a real connection does.
    /// </summary>
    private sealed class WorkingConsumer(Broker broker, ConcurrentBag<string> received) : Consumer
    {
        private readonly BlockingCollection<BrokerMessage> _inbox = [];

        public override string Description => "worker";

        public Task Run() => Task.Run(() =>
        {
            foreach (var message in _inbox.GetConsumingEnumerable())
            {
                received.Add(message.Body);
                broker.Confirm(this, message.Queue, message.Id);
            }
        });

        public void Stop() => _inbox.CompleteAdding();

        protected override void Deliver(BrokerMessage message) => _inbox.Add(message);

        protected override void DeliverResponse(BrokerMessage message)
        {
        }
    }

    [Fact]
    public async Task Pushed_and_asking_consumers_together_receive_every_message_exactly_once()
    {
        const int perPublisher = 5_000;
        const int publishers = 4;
        var broker = new Broker();
        var received = new ConcurrentBag<string>();
        var publishing = true;

        var pushed = Enumerable.Range(0, 4).Select(_ => new WorkingConsumer(broker, received)).ToArray();
        var pushedTasks = pushed.Select(consumer => consumer.Run()).ToArray();
        foreach (var consumer in pushed)
        {
            broker.Bind(consumer, "orders");
            broker.Bind(consumer, "invoices");
        }

        var asking = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (true)
            {
                var message = broker.Take("orders") ?? broker.Take("invoices");
                if (message is not null)
                    received.Add(message.Body);
                else if (!Volatile.Read(ref publishing))
                    break;
            }
        })).ToArray();

        await Task.WhenAll(Enumerable.Range(0, publishers).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < perPublisher; i++)
                broker.Publish(i % 2 == 0 ? "orders" : "invoices", $"{p}:{i}");
        })));

        var total = perPublisher * publishers;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (received.Count < total && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Volatile.Write(ref publishing, false);
        await Task.WhenAll(asking);
        foreach (var consumer in pushed)
            consumer.Stop();
        await Task.WhenAll(pushedTasks);

        Assert.Equal(total, received.Count);
        Assert.Equal(total, received.Distinct().Count());
        Assert.All(broker.GetQueues(), queue => Assert.Equal((0, 0), (queue.Pending, queue.Processing)));
    }

    [Fact]
    public async Task Consumers_coming_and_going_never_cause_a_message_to_be_processed_twice()
    {
        const int total = 20_000;
        var broker = new Broker(new BrokerOptions { DeadLetterLimit = int.MaxValue });
        var received = new ConcurrentBag<string>();
        var running = true;

        var churn = Task.Run(async () =>
        {
            while (Volatile.Read(ref running))
            {
                var consumer = new WorkingConsumer(broker, received);
                var work = consumer.Run();
                broker.Bind(consumer, "orders");
                await Task.Delay(2);
                broker.Disconnect(consumer);
                consumer.Stop();
                await work;
            }
        });

        var steady = new WorkingConsumer(broker, received);
        var steadyWork = steady.Run();
        broker.Bind(steady, "orders");

        for (var i = 0; i < total; i++)
            broker.Publish("orders", i.ToString());

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (broker.GetQueues().Single().Pending > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Volatile.Write(ref running, false);
        await churn;
        await Task.Delay(100);
        steady.Stop();
        await steadyWork;

        // A consumer that left while holding a message may or may not have processed it; either
        // way the message is in exactly one place: processed, or among the dead letters.
        var dead = broker.GetDeadLetters("orders").Select(l => l.Body).ToHashSet();
        Assert.Equal(received.Count, received.Distinct().Count());
        Assert.Equal(total, received.Concat(dead).Distinct().Count());
    }
}
