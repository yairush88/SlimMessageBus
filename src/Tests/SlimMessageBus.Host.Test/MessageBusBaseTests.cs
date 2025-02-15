﻿namespace SlimMessageBus.Host.Test;

using SlimMessageBus.Host.Config;
using SlimMessageBus.Host.DependencyResolver;
using SlimMessageBus.Host.Interceptor;
using SlimMessageBus.Host.Serialization;
using SlimMessageBus.Host.Serialization.Json;
using System;
using System.IO;
using System.Reflection.PortableExecutable;

public class RequestA : IRequestMessage<ResponseA>
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
}

public class ResponseA
{
    public string Id { get; set; }
}

public class RequestB : IRequestMessage<ResponseB> { }

public class ResponseB { }

public class MessageBusBaseTests : IDisposable
{
    private MessageBusBuilder BusBuilder { get; }
    private readonly Lazy<MessageBusTested> _busLazy;
    private MessageBusTested Bus => _busLazy.Value;
    private readonly DateTimeOffset _timeZero;
    private DateTimeOffset _timeNow;

    private const int TimeoutFor5 = 5;
    private const int TimeoutDefault10 = 10;
    private readonly Mock<IDependencyResolver> _dependencyResolverMock;

    public IList<ProducedMessage> _producedMessages;

    public record ProducedMessage(Type MessageType, string Path, object Message);

    public MessageBusBaseTests()
    {
        _timeZero = DateTimeOffset.Now;
        _timeNow = _timeZero;

        _producedMessages = new List<ProducedMessage>();

        _dependencyResolverMock = new Mock<IDependencyResolver>();
        _dependencyResolverMock.Setup(x => x.Resolve(It.IsAny<Type>())).Returns((Type t) =>
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>)) return Enumerable.Empty<object>();
            return null;
        });

        BusBuilder = MessageBusBuilder.Create()
            .Produce<RequestA>(x =>
            {
                x.DefaultTopic("a-requests");
                x.DefaultTimeout(TimeSpan.FromSeconds(TimeoutFor5));
            })
            .Produce<RequestB>(x =>
            {
                x.DefaultTopic("b-requests");
            })
            .ExpectRequestResponses(x =>
            {
                x.ReplyToTopic("app01-responses");
                x.DefaultTimeout(TimeSpan.FromSeconds(TimeoutDefault10));
            })
            .WithDependencyResolver(_dependencyResolverMock.Object)
            .WithSerializer(new JsonMessageSerializer())
            .WithProvider(s =>
            {
                var bus = new MessageBusTested(s)
                {
                    // provide current time
                    CurrentTimeProvider = () => _timeNow,
                    OnProduced = (mt, n, m) => _producedMessages.Add(new(mt, n, m))
                };
                return bus;
            });

        _busLazy = new Lazy<MessageBusTested>(() => (MessageBusTested)BusBuilder.Build());
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_busLazy.IsValueCreated)
            {
                _busLazy.Value.Dispose();
            }
        }
    }

    [Fact]
    public void When_Create_Given_ConfigurationThatDeclaresSameMessageTypeMoreThanOnce_Then_ExceptionIsThrown()
    {
        // arrange
        BusBuilder.Produce<RequestA>(x => x.DefaultTopic("default-topic"));
        BusBuilder.Produce<RequestA>(x => x.DefaultTopic("default-topic-2"));

        // act
        Action busCreation = () => BusBuilder.Build();

        // assert
        busCreation.Should().Throw<ConfigurationMessageBusException>()
            .WithMessage("*was declared more than once*");
    }

    [Fact]
    public async Task When_NoTimeoutProvided_Then_TakesDefaultTimeoutForRequestTypeAsync()
    {
        // arrange
        var ra = new RequestA();
        var rb = new RequestB();

        // act
        var raTask = Bus.Send(ra);
        var rbTask = Bus.Send(rb);

        // after 10 seconds
        _timeNow = _timeZero.AddSeconds(TimeoutFor5 + 1);
        Bus.TriggerPendingRequestCleanup();

        // assert
        await WaitForTasks(2000, raTask, rbTask);

        raTask.IsCanceled.Should().BeTrue();
        rbTask.IsCanceled.Should().BeFalse();

        // adter 20 seconds
        _timeNow = _timeZero.AddSeconds(TimeoutDefault10 + 1);
        Bus.TriggerPendingRequestCleanup();

        await WaitForTasks(2000, rbTask);

        // assert
        rbTask.IsCanceled.Should().BeTrue();
    }

    [Fact]
    public async Task When_ResponseArrives_Then_ResolvesPendingRequestAsync()
    {
        // arrange
        var r = new RequestA();

        Bus.OnReply = (type, topic, request) =>
        {
            if (topic == "a-requests")
            {
                var req = (RequestA)request;
                return new ResponseA { Id = req.Id };
            }
            return null;
        };

        // act
        var rTask = Bus.Send(r);
        await WaitForTasks(2000, rTask);
        Bus.TriggerPendingRequestCleanup();

        // assert
        rTask.IsCompleted.Should().BeTrue("Response should be completed");
        r.Id.Should().Be(rTask.Result.Id);

        Bus.PendingRequestsCount.Should().Be(0, "There should be no pending requests");
    }

    [Fact]
    public async Task When_ResponseArrivesTooLate_Then_ExpiresPendingRequestAsync()
    {
        // arrange
        var r1 = new RequestA();
        var r2 = new RequestA();
        var r3 = new RequestA();

        Bus.OnReply = (type, topic, request) =>
        {
            if (topic == "a-requests")
            {
                var req = (RequestA)request;
                // resolve only r1 request
                if (req.Id == r1.Id)
                {
                    return new ResponseA { Id = req.Id };
                }
            }
            return null;
        };

        // act
        var r1Task = Bus.Send(r1);
        var r2Task = Bus.Send(r2, timeout: TimeSpan.FromSeconds(1));
        var r3Task = Bus.Send(r3);

        // 2 seconds later
        _timeNow = _timeZero.AddSeconds(2);
        Bus.TriggerPendingRequestCleanup();

        await WaitForTasks(2000, r1Task, r2Task, r3Task);

        // assert
        r1Task.IsCompleted.Should().BeTrue("Response 1 should be completed");
        r2Task.IsCanceled.Should().BeTrue("Response 2 should be canceled");
        r3Task.IsCompleted.Should().BeFalse("Response 3 should still be pending");
        Bus.PendingRequestsCount.Should().Be(1, "There should be only 1 pending request");
    }

    [Fact]
    public async Task When_CancellationTokenCancelled_Then_CancellsPendingRequest()
    {
        // arrange
        var r1 = new RequestA();
        var r2 = new RequestA();

        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();

        cts2.Cancel();
        var r1Task = Bus.Send(r1, cancellationToken: cts1.Token);
        var r2Task = Bus.Send(r2, cancellationToken: cts2.Token);

        // act
        Bus.TriggerPendingRequestCleanup();
        await Task.WhenAny(r1Task, r2Task);

        // assert
        r1Task.IsCompleted.Should().BeFalse("Request 1 is still pending");

        r2Task.IsCanceled.Should().BeTrue("Request 2 was canceled");
        r2Task.IsFaulted.Should().BeFalse("Request 2 was canceled");
    }

    private static async Task WaitForTasks(int millis, params Task[] tasks)
    {
        try
        {
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(millis));
        }
        catch (Exception)
        {
            // swallow
        }
    }

    [Fact]
    public async Task When_Produce_DerivedMessage_Given_OnlyBaseMessageConfigured_Then_BaseMessageTypeIsSentToSerializer()
    {
        // arrange
        var messageSerializerMock = new Mock<IMessageSerializer>();
        messageSerializerMock.Setup(x => x.Serialize(It.IsAny<Type>(), It.IsAny<object>())).Returns(new byte[0]);

        var someMessageTopic = "some-messages";

        BusBuilder
            .Produce<SomeMessage>(x => x.DefaultTopic(someMessageTopic))
            .WithSerializer(messageSerializerMock.Object);

        var m1 = new SomeMessage();
        var m2 = new SomeDerivedMessage();
        var m3 = new SomeDerived2Message();

        // act
        await Bus.Publish(m1);
        await Bus.Publish(m2);
        await Bus.Publish(m3);

        // assert
        _producedMessages.Count.Should().Be(3);

        _producedMessages.Should().ContainSingle(x => x.MessageType == typeof(SomeMessage) && ReferenceEquals(x.Message, m1) && x.Path == someMessageTopic);
        _producedMessages.Should().ContainSingle(x => x.MessageType == typeof(SomeDerivedMessage) && ReferenceEquals(x.Message, m2) && x.Path == someMessageTopic);
        _producedMessages.Should().ContainSingle(x => x.MessageType == typeof(SomeDerived2Message) && ReferenceEquals(x.Message, m3) && x.Path == someMessageTopic);

        messageSerializerMock.Verify(x => x.Serialize(typeof(SomeMessage), m1), Times.Once);
        messageSerializerMock.Verify(x => x.Serialize(typeof(SomeMessage), m2), Times.Once);
        messageSerializerMock.Verify(x => x.Serialize(typeof(SomeMessage), m3), Times.Once);
        messageSerializerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task When_Produce_DerivedMessage_Given_OnlyBaseMessageConfigured_Then_BaseMessageProducerConfigUsed()
    {
        // arrange
        var someMessageTopic = "some-messages";

        BusBuilder
            .Produce<SomeMessage>(x => x.DefaultTopic(someMessageTopic));

        var m = new SomeDerivedMessage();

        // act
        await Bus.Publish(m);

        // assert
        _producedMessages.Count.Should().Be(1);
        _producedMessages.Should().ContainSingle(x => x.MessageType == typeof(SomeDerivedMessage) && ReferenceEquals(x.Message, m) && x.Path == someMessageTopic);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task When_Publish_DerivedMessage_Given_DeriveMessageConfigured_Then_DerivedMessageProducerConfigUsed(int caseId)
    {
        // arrange
        var someMessageTopic = "some-messages";
        var someMessageDerived2Topic = "some-messages-2";

        BusBuilder
            .Produce<SomeMessage>(x => x.DefaultTopic(someMessageTopic))
            .Produce<SomeDerived2Message>(x => x.DefaultTopic(someMessageDerived2Topic));

        var m = new SomeDerived2Message();

        // act
        if (caseId == 1)
        {
            // act
            await Bus.Publish(m);

        }

        if (caseId == 2)
        {
            // act
            await Bus.Publish<SomeMessage>(m);
        }

        if (caseId == 3)
        {
            // act
            await Bus.Publish<ISomeMessageMarkerInterface>(m);
        }

        // assert
        _producedMessages.Count.Should().Be(1);
        _producedMessages.Should().ContainSingle(x => x.MessageType == typeof(SomeDerived2Message) && ReferenceEquals(x.Message, m) && x.Path == someMessageDerived2Topic);
    }

    [Fact]
    public void When_GetProducerSettings_Given_MessageWasDeclared_Then_GetProducerSettingsShouldNotReturnNull()
    {
        // arrange
        var someMessageTopic = "some-messages";

        BusBuilder
            .Produce<SomeMessage>(x => x.DefaultTopic(someMessageTopic))
            .Produce<SomeRequest>(x => x.DefaultTopic(someMessageTopic));

        // act
        var producerSettings1 = Bus.Public_GetProducerSettings(typeof(SomeMessage));
        var producerSettings2 = Bus.Public_GetProducerSettings(typeof(SomeRequest));

        // assert
        producerSettings1.Should().NotBeNull();
        producerSettings2.Should().NotBeNull();
    }

    [Fact]
    public void When_GetProducerSettings_Given_RequestMessageHandled_Then_GetProducerSettingsShouldReturnNull()
    {
        // arrange
        var someMessageTopic = "some-messages";

        BusBuilder
            .Handle<SomeRequest, SomeResponse>(x => x.Topic(someMessageTopic).WithHandler<SomeRequestMessageHandler>());

        // act
        var producerSettings = Bus.Public_GetProducerSettings(typeof(SomeResponse));

        // assert
        producerSettings.Should().NotBeNull();
    }

    [Fact]
    public void When_Produce_Message_Given_MessageNotDeclared_Then_GetProducerSettingsShouldThrowException()
    {
        // arrange
        var someMessageTopic = "some-messages";

        BusBuilder
            .Produce<SomeMessage>(x => x.DefaultTopic(someMessageTopic));

        // act
        Action action = () => Bus.Public_GetProducerSettings(typeof(SomeRequest));

        // assert
        action.Should().Throw<ProducerMessageBusException>();
    }

    [Fact]
    public async Task When_Publish_Given_Disposed_Then_ThrowsException()
    {
        // arrange
        Bus.Dispose();

        // act
        Func<Task> act = async () => await Bus.Publish(new SomeMessage()).ConfigureAwait(false);
        Func<Task> actWithTopic = async () => await Bus.Publish(new SomeMessage(), "some-topic").ConfigureAwait(false);

        // assert
        await act.Should().ThrowAsync<MessageBusException>();
        await actWithTopic.Should().ThrowAsync<MessageBusException>();
    }

    [Fact]
    public async Task When_Send_Given_Disposed_Then_ThrowsException()
    {
        // arrange
        Bus.Dispose();

        // act
        Func<Task> act = async () => await Bus.Send(new SomeRequest()).ConfigureAwait(false);
        Func<Task> actWithTopic = async () => await Bus.Send(new SomeRequest(), "some-topic").ConfigureAwait(false);

        // assert
        await act.Should().ThrowAsync<MessageBusException>();
        await actWithTopic.Should().ThrowAsync<MessageBusException>();
    }

    [Fact]
    public async Task When_Publish_Or_Send_Then_OnMessageProducedIsCalled_AtTheProducerLevel_And_AtTheBusLevel()
    {
        // arrange
        var someMessageTopic = "some-messages";
        var someRequestTopic = "some-requests";

        var onMessageProducedMock = new Mock<Action<IMessageBus, ProducerSettings, object, string>>();

        BusBuilder
            .Produce<SomeMessage>(x =>
            {
                x.DefaultTopic(someMessageTopic);
                x.AttachEvents(events =>
                {
                    events.OnMessageProduced = onMessageProducedMock.Object;
                });
            })
            .Produce<SomeRequest>(x =>
            {
                x.DefaultTopic(someRequestTopic);
                x.AttachEvents(events =>
                {
                    events.OnMessageProduced = onMessageProducedMock.Object;
                });
            })
            .AttachEvents(events =>
            {
                events.OnMessageProduced = onMessageProducedMock.Object;
            });

        Bus.OnReply = (type, topic, request) =>
        {
            if (topic == someRequestTopic)
            {
                return new SomeResponse();
            }
            return null;
        };

        var m = new SomeMessage();
        var r = new SomeRequest();

        // act

        // act
        await Bus.Publish(m);
        await Bus.Send(r);

        // assert
        onMessageProducedMock.Verify(
            x => x(Bus, It.IsAny<ProducerSettings>(), m, someMessageTopic), Times.Exactly(2)); // callback twice - at the producer and bus level

        onMessageProducedMock.Verify(
            x => x(Bus, It.IsAny<ProducerSettings>(), r, someRequestTopic), Times.Exactly(2)); // callback twice - at the producer and bus level
    }

    [Theory]
    [InlineData(1, null, null)]   // no interceptors
    [InlineData(1, true, true)]   // both interceptors call next()
    [InlineData(1, true, null)]   // producer interceptor calls next(), other does not exist
    [InlineData(1, null, true)]   // publish interceptor calls next(), other does not exist
    [InlineData(0, false, false)] // none of the interceptors calls next()
    [InlineData(0, false, null)]  // producer interceptor does not call next()
    [InlineData(0, null, false)]  // publish interceptor does not call next()
    public async Task When_Publish_Given_InterceptorsInDI_Then_InterceptorInfluenceIfTheMessageIsDelivered(
        int producedMessages, bool? producerInterceptorCallsNext, bool? publishInterceptorCallsNext)
    {
        // arrange
        var topic = "some-messages";

        BusBuilder
            .Produce<SomeMessage>(x => x.DefaultTopic(topic));

        var m = new SomeDerivedMessage();

        var producerInterceptorMock = new Mock<IProducerInterceptor<SomeDerivedMessage>>();
        producerInterceptorMock.Setup(x => x.OnHandle(m, It.IsAny<Func<Task<object>>>(), It.IsAny<IProducerContext>()))
            .Returns((SomeDerivedMessage m, Func<Task<object>> next, IProducerContext context)
                => producerInterceptorCallsNext == true ? next() : Task.FromResult<object>(null));

        var publishInterceptorMock = new Mock<IPublishInterceptor<SomeDerivedMessage>>();
        publishInterceptorMock.Setup(x => x.OnHandle(m, It.IsAny<Func<Task>>(), It.IsAny<IProducerContext>()))
            .Returns((SomeDerivedMessage m, Func<Task> next, IProducerContext context)
                => publishInterceptorCallsNext == true ? next() : Task.CompletedTask);

        if (producerInterceptorCallsNext != null)
        {
            _dependencyResolverMock
                .Setup(x => x.Resolve(typeof(IEnumerable<IProducerInterceptor<SomeDerivedMessage>>)))
                .Returns(new[] { producerInterceptorMock.Object });
        }

        if (publishInterceptorCallsNext != null)
        {
            _dependencyResolverMock
                .Setup(x => x.Resolve(typeof(IEnumerable<IPublishInterceptor<SomeDerivedMessage>>)))
                .Returns(new[] { publishInterceptorMock.Object });
        }

        // act
        await Bus.Publish(m);

        // assert

        // message delivered
        _producedMessages.Count.Should().Be(producedMessages);

        _dependencyResolverMock.Verify(x => x.Resolve(typeof(IEnumerable<IProducerInterceptor<SomeDerivedMessage>>)), Times.Once);
        _dependencyResolverMock.Verify(x => x.Resolve(typeof(IEnumerable<IProducerInterceptor<SomeMessage>>)), Times.Never);
        _dependencyResolverMock.Verify(x => x.Resolve(typeof(IEnumerable<IPublishInterceptor<SomeDerivedMessage>>)), Times.Once);
        _dependencyResolverMock.Verify(x => x.Resolve(typeof(IEnumerable<IPublishInterceptor<SomeMessage>>)), Times.Never);
        _dependencyResolverMock.Verify(x => x.Resolve(typeof(IEnumerable<IMessageBusLifecycleInterceptor>)), Times.Between(0, 2, Moq.Range.Inclusive));
        _dependencyResolverMock.Verify(x => x.Resolve(typeof(ILoggerFactory)), Times.Once);
        _dependencyResolverMock.VerifyNoOtherCalls();

        if (producerInterceptorCallsNext != null)
        {
            producerInterceptorMock.Verify(x => x.OnHandle(m, It.IsAny<Func<Task<object>>>(), It.IsAny<IProducerContext>()), Times.Once);
        }
        producerInterceptorMock.VerifyNoOtherCalls();

        if (publishInterceptorCallsNext != null)
        {
            // Publish interceptor is called after Producer interceptor, if producer does not call next() the publish interceptor does not get a chance to fire
            if (producerInterceptorCallsNext == null || producerInterceptorCallsNext == true)
            {
                publishInterceptorMock.Verify(x => x.OnHandle(m, It.IsAny<Func<Task>>(), It.IsAny<IProducerContext>()), Times.Once);
            }
        }
        publishInterceptorMock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1, null, null)]   // no interceptors
    [InlineData(1, true, true)]   // both interceptors call next()
    [InlineData(1, true, null)]   // producer interceptor calls next(), other does not exist
    [InlineData(1, null, true)]   // send interceptor calls next(), other does not exist
    [InlineData(0, false, false)] // none of the interceptors calls next()
    [InlineData(0, false, null)]  // producer interceptor does not call next()
    [InlineData(0, null, false)]  // send interceptor does not call next()
    public async Task When_Send_Given_InterceptorsInDI_Then_InterceptorInfluenceIfTheMessageIsDelivered(
        int producedMessages, bool? producerInterceptorCallsNext, bool? sendInterceptorCallsNext)
    {
        // arrange
        var topic = "a-requests";

        var request = new RequestA();

        Bus.OnReply = (type, topicOnReply, requestOnReply) =>
        {
            if (topicOnReply == topic)
            {
                var req = (RequestA)requestOnReply;
                // resolve only r1 request
                if (req.Id == request.Id)
                {
                    return new ResponseA { Id = req.Id };
                }
            }
            return null;
        };

        var producerInterceptorMock = new Mock<IProducerInterceptor<RequestA>>();
        producerInterceptorMock
            .Setup(x => x.OnHandle(request, It.IsAny<Func<Task<object>>>(), It.IsAny<IProducerContext>()))
            .Returns((RequestA m, Func<Task<object>> next, IProducerContext context)
                => producerInterceptorCallsNext == true ? next() : Task.FromResult<object>(null));

        var sendInterceptorMock = new Mock<ISendInterceptor<RequestA, ResponseA>>();
        sendInterceptorMock
            .Setup(x => x.OnHandle(request, It.IsAny<Func<Task<ResponseA>>>(), It.IsAny<IProducerContext>()))
            .Returns((RequestA m, Func<Task<ResponseA>> next, IProducerContext context)
                => sendInterceptorCallsNext == true ? next() : Task.FromResult<ResponseA>(null));

        if (producerInterceptorCallsNext != null)
        {
            _dependencyResolverMock
                .Setup(x => x.Resolve(typeof(IEnumerable<IProducerInterceptor<RequestA>>)))
                .Returns(new[] { producerInterceptorMock.Object });
        }

        if (sendInterceptorCallsNext != null)
        {
            _dependencyResolverMock
                .Setup(x => x.Resolve(typeof(IEnumerable<ISendInterceptor<RequestA, ResponseA>>)))
                .Returns(new[] { sendInterceptorMock.Object });
        }

        // act
        var response = await Bus.Send(request);

        // assert

        // message delivered
        _producedMessages.Count.Should().Be(producedMessages);

        if (producedMessages > 0)
        {
            response.Id.Should().Be(request.Id);
        }
        else
        {
            // when the interceptor does not call next() the response resolved to default (null)
            response.Should().BeNull();
        }

        _dependencyResolverMock.Verify(x => x.Resolve(typeof(IEnumerable<IProducerInterceptor<RequestA>>)), Times.Once);
        _dependencyResolverMock.Verify(x => x.Resolve(typeof(IEnumerable<ISendInterceptor<RequestA, ResponseA>>)), Times.Once);
        _dependencyResolverMock.Verify(x => x.Resolve(typeof(IEnumerable<IMessageBusLifecycleInterceptor>)), Times.Between(0, 2, Moq.Range.Inclusive));
        _dependencyResolverMock.Verify(x => x.Resolve(typeof(ILoggerFactory)), Times.Once);
        _dependencyResolverMock.VerifyNoOtherCalls();

        if (producerInterceptorCallsNext != null)
        {
            producerInterceptorMock.Verify(x => x.OnHandle(request, It.IsAny<Func<Task<object>>>(), It.IsAny<IProducerContext>()), Times.Once);
        }
        producerInterceptorMock.VerifyNoOtherCalls();

        if (sendInterceptorCallsNext != null)
        {
            // Publish interceptor is called after Producer interceptor, if producer does not call next() the publish interceptor does not get a chance to fire
            if (producerInterceptorCallsNext == null || producerInterceptorCallsNext == true)
            {
                sendInterceptorMock.Verify(x => x.OnHandle(request, It.IsAny<Func<Task<ResponseA>>>(), It.IsAny<IProducerContext>()), Times.Once);
            }
        }
        sendInterceptorMock.VerifyNoOtherCalls();
    }


    public void When_GetChildBus_Then_ReturnsSelf_Given_NameIsNull()
    {
        var compositeMessageBus = Bus is ICompositeMessageBus;

    }


}
