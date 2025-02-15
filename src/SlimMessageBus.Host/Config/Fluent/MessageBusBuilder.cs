namespace SlimMessageBus.Host.Config;

public class MessageBusBuilder
{
    /// <summary>
    /// The current settings that are being built.
    /// </summary>
    public MessageBusSettings Settings { get; } = new();

    /// <summary>
    /// Represents global configurators that are part for this builder.
    /// </summary>
    public IEnumerable<IMessageBusConfigurator> Configurators { get; set; } = Enumerable.Empty<IMessageBusConfigurator>();

    public IDictionary<string, Action<MessageBusBuilder>> ChildBuilders { get; } = new Dictionary<string, Action<MessageBusBuilder>>();

    /// <summary>
    /// The bus factory method.
    /// </summary>
    public Func<MessageBusSettings, IMessageBus> BusFactory { get; private set; }

    protected MessageBusBuilder()
    {
    }

    protected MessageBusBuilder(MessageBusBuilder other)
    {
        Settings = other.Settings;
        Configurators = other.Configurators;
        ChildBuilders = other.ChildBuilders;
        BusFactory = other.BusFactory;
    }

    public static MessageBusBuilder Create() => new();

    public MessageBusBuilder MergeFrom(MessageBusSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        Settings.MergeFrom(settings);
        return this;
    }

    /// <summary>
    /// Configures (declares) the production (publishing for pub/sub or request sending in request/response) of a message 
    /// </summary>
    /// <typeparam name="T">Type of the message</typeparam>
    /// <param name="producerBuilder"></param>
    /// <returns></returns>
    public MessageBusBuilder Produce<T>(Action<ProducerBuilder<T>> producerBuilder)
    {
        if (producerBuilder == null) throw new ArgumentNullException(nameof(producerBuilder));

        var item = new ProducerSettings();
        producerBuilder(new ProducerBuilder<T>(item));
        Settings.Producers.Add(item);
        return this;
    }

    /// <summary>
    /// Configures (declares) the production (publishing for pub/sub or request sending in request/response) of a message 
    /// </summary>
    /// <param name="messageType">Type of the message</param>
    /// <param name="producerBuilder"></param>
    /// <returns></returns>
    public MessageBusBuilder Produce(Type messageType, Action<ProducerBuilder<object>> producerBuilder)
    {
        if (producerBuilder == null) throw new ArgumentNullException(nameof(producerBuilder));

        var item = new ProducerSettings();
        producerBuilder(new ProducerBuilder<object>(item, messageType));
        Settings.Producers.Add(item);
        return this;
    }

    /// <summary>
    /// Configures (declares) the consumer of given message types in pub/sub or queue communication.
    /// </summary>
    /// <typeparam name="TMessage">Type of message</typeparam>
    /// <param name="consumerBuilder"></param>
    /// <returns></returns>
    public MessageBusBuilder Consume<TMessage>(Action<ConsumerBuilder<TMessage>> consumerBuilder)
    {
        if (consumerBuilder == null) throw new ArgumentNullException(nameof(consumerBuilder));

        consumerBuilder(new ConsumerBuilder<TMessage>(Settings));
        return this;
    }

    /// <summary>
    /// Configures (declares) the consumer of given message types in pub/sub or queue communication.
    /// </summary>
    /// <param name="messageType">Type of message</param>
    /// <param name="consumerBuilder"></param>
    /// <returns></returns>
    public MessageBusBuilder Consume(Type messageType, Action<ConsumerBuilder<object>> consumerBuilder)
    {
        if (consumerBuilder == null) throw new ArgumentNullException(nameof(consumerBuilder));

        consumerBuilder(new ConsumerBuilder<object>(Settings, messageType));
        return this;
    }

    /// <summary>
    /// Configures (declares) the handler of a given request message type in request-response communication.
    /// </summary>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    /// <param name="handlerBuilder"></param>
    /// <returns></returns>
    public MessageBusBuilder Handle<TRequest, TResponse>(Action<HandlerBuilder<TRequest, TResponse>> handlerBuilder)
    {
        if (handlerBuilder == null) throw new ArgumentNullException(nameof(handlerBuilder));

        handlerBuilder(new HandlerBuilder<TRequest, TResponse>(Settings));
        return this;
    }

    /// <summary>
    /// Configures (declares) the handler of a given request message type in request-response communication.
    /// </summary>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    /// <param name="handlerBuilder"></param>
    /// <returns></returns>
    public MessageBusBuilder Handle(Type requestType, Type responseType, Action<HandlerBuilder<object, object>> handlerBuilder)
    {
        if (requestType == null) throw new ArgumentNullException(nameof(requestType));
        if (responseType == null) throw new ArgumentNullException(nameof(responseType));
        if (handlerBuilder == null) throw new ArgumentNullException(nameof(handlerBuilder));

        handlerBuilder(new HandlerBuilder<object, object>(Settings, requestType, responseType));
        return this;
    }

    public MessageBusBuilder ExpectRequestResponses(Action<RequestResponseBuilder> reqRespBuilder)
    {
        if (reqRespBuilder == null) throw new ArgumentNullException(nameof(reqRespBuilder));

        var item = new RequestResponseSettings();
        reqRespBuilder(new RequestResponseBuilder(item));
        Settings.RequestResponse = item;
        return this;
    }

    public MessageBusBuilder WithLoggerFacory(ILoggerFactory loggerFactory)
    {
        Settings.LoggerFactory = loggerFactory;
        return this;
    }

    public MessageBusBuilder WithSerializer(IMessageSerializer serializer)
    {
        Settings.Serializer = serializer;
        return this;
    }

    public MessageBusBuilder WithDependencyResolver(IDependencyResolver dependencyResolver)
    {
        Settings.DependencyResolver = dependencyResolver ?? throw new ArgumentNullException(nameof(dependencyResolver));
        return this;
    }

    public MessageBusBuilder WithProvider(Func<MessageBusSettings, IMessageBus> provider)
    {
        BusFactory = provider ?? throw new ArgumentNullException(nameof(provider));
        return this;
    }

    public MessageBusBuilder Do(Action<MessageBusBuilder> builder)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));

        builder(this);
        return this;
    }

    public MessageBusBuilder AttachEvents(Action<IProducerEvents> eventsConfig)
    {
        if (eventsConfig == null) throw new ArgumentNullException(nameof(eventsConfig));

        eventsConfig(Settings);
        return this;
    }

    public MessageBusBuilder AttachEvents(Action<IConsumerEvents> eventsConfig)
    {
        if (eventsConfig == null) throw new ArgumentNullException(nameof(eventsConfig));

        eventsConfig(Settings);
        return this;
    }

    public MessageBusBuilder AttachEvents(Action<IBusEvents> eventsConfig)
    {
        if (eventsConfig == null) throw new ArgumentNullException(nameof(eventsConfig));

        eventsConfig(Settings);
        return this;
    }

    /// <summary>
    /// Sets the default enable (or disable) creation of DI child scope for each meesage.
    /// </summary>
    /// <param name="enabled"></param>
    /// <returns></returns>
    public MessageBusBuilder PerMessageScopeEnabled(bool enabled)
    {
        Settings.IsMessageScopeEnabled = enabled;
        return this;
    }

    public MessageBusBuilder WithMessageTypeResolver(IMessageTypeResolver messageTypeResolver)
    {
        Settings.MessageTypeResolver = messageTypeResolver ?? throw new ArgumentNullException(nameof(messageTypeResolver));
        return this;
    }

    /// <summary>
    /// Hook called whenver message is being produced. Can be used to add (or mutate) message headers.
    /// </summary>
    public MessageBusBuilder WithHeaderModifier(Action<IDictionary<string, object>, object> headerModifierAction)
    {
        Settings.HeaderModifier = headerModifierAction ?? throw new ArgumentNullException(nameof(headerModifierAction));
        return this;
    }

    /// <summary>
    /// Enables or disabled the auto statrt of message consumption upon bus creation. If false, then you need to call the .Start() on the bus to start consuming messages.
    /// </summary>
    /// <param name="enabled"></param>
    public MessageBusBuilder AutoStartConsumersEnabled(bool enabled)
    {
        Settings.AutoStartConsumers = enabled;
        return this;
    }

    public MessageBusBuilder AddChildBus(string busName, Action<MessageBusBuilder> builderAction)
    {
        if (busName is null) throw new ArgumentNullException(nameof(busName));
        if (builderAction is null) throw new ArgumentNullException(nameof(builderAction));

        if (ChildBuilders.ContainsKey(busName))
        {
            throw new ConfigurationMessageBusException($"The child bus with name {busName} has been already declared");
        }
        ChildBuilders.Add(busName, builderAction);
        return this;
    }

    public IMessageBus Build()
    {
        if (BusFactory is null)
        {
            throw new ConfigurationMessageBusException("The bus provider was not configured. Check the MessageBus configuration.");
        }

        foreach (var configurator in Configurators)
        {
            configurator.Configure(this, Settings.Name);
        }

        return BusFactory(Settings);
    }
}