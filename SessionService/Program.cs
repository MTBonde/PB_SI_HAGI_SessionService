using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StackExchange.Redis;
using System.Text;
using System.Text.Json;
using SessionService;

Console.WriteLine($"SessionService v{ServiceVersion.Current} starting...");

var redisHost = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "localhost:6379";
var rabbitMqHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";
var sessionTimeoutHours = int.Parse(Environment.GetEnvironmentVariable("SESSION_TIMEOUT_HOURS") ?? "1");

Console.WriteLine($"Connecting to Redis at {redisHost}...");
var redis = ConnectionMultiplexer.Connect(redisHost);
var redisDb = redis.GetDatabase();
Console.WriteLine("Connected to Redis");

Console.WriteLine($"Connecting to RabbitMQ at {rabbitMqHost}...");

try
{
    var factory = new ConnectionFactory
    {
        HostName = rabbitMqHost,
        Port = 5672,
        RequestedConnectionTimeout = TimeSpan.FromSeconds(30),
        AutomaticRecoveryEnabled = true
    };

    using var connection = await factory.CreateConnectionAsync();
    using var channel = await connection.CreateChannelAsync();

    Console.WriteLine("SessionService connected to RabbitMQ");

    // Declare exchange: players (direct)
    await channel.ExchangeDeclareAsync(
        exchange: "players",
        type: ExchangeType.Direct,
        durable: true,
        autoDelete: false);
    Console.WriteLine("Declared exchange: players (direct, durable)");

    // Declare exchange: relay.session.events (topic)
    await channel.ExchangeDeclareAsync(
        exchange: "relay.session.events",
        type: ExchangeType.Topic,
        durable: true,
        autoDelete: false);
    Console.WriteLine("Declared exchange: relay.session.events (topic, durable)");

    // Declare queue for login events
    var queueDeclareResult = await channel.QueueDeclareAsync(
        queue: "session.login.queue",
        durable: true,
        exclusive: false,
        autoDelete: false);
    var queueName = queueDeclareResult.QueueName;
    Console.WriteLine($"Declared queue: {queueName}");

    // Bind queue to players exchange
    await channel.QueueBindAsync(
        queue: queueName,
        exchange: "players",
        routingKey: "player.login");

    Console.WriteLine("Queue bound to players exchange with routing key 'player.login'");

    // Declare queue for presence events (user connected/disconnected)
    var presenceQueueResult = await channel.QueueDeclareAsync(
        queue: "session.presence.queue",
        durable: true,
        exclusive: false,
        autoDelete: false);
    var presenceQueueName = presenceQueueResult.QueueName;
    Console.WriteLine($"Declared queue: {presenceQueueName}");

    // Bind queue to relay.session.events exchange for user.connected
    await channel.QueueBindAsync(
        queue: presenceQueueName,
        exchange: "relay.session.events",
        routingKey: "user.connected");

    // Bind queue to relay.session.events exchange for user.disconnected
    await channel.QueueBindAsync(
        queue: presenceQueueName,
        exchange: "relay.session.events",
        routingKey: "user.disconnected");

    Console.WriteLine("Queue bound to relay.session.events exchange with routing keys 'user.connected' and 'user.disconnected'");

    // Create message consumer to process incoming login events
    var consumer = new AsyncEventingBasicConsumer(channel);

    consumer.ReceivedAsync += async (model, ea) =>
    {
        try
        {
            var body = ea.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);
            var loginEvent = JsonSerializer.Deserialize<LoginEvent>(message);

            if (loginEvent == null || string.IsNullOrEmpty(loginEvent.UserId))
            {
                Console.WriteLine("Invalid login event received");
                return;
            }

            var existingSessionKey = $"user:{loginEvent.UserId}:session";
            var existingSession = await redisDb.StringGetAsync(existingSessionKey);

            if (existingSession.HasValue)
            {
                Console.WriteLine($"User {loginEvent.UserId} already has active session: {existingSession}. Terminating old session.");

                await redisDb.KeyDeleteAsync(existingSessionKey);

                await PublishSessionEventAsync(channel, "session.terminated", loginEvent.UserId, existingSession.ToString());
            }

            var newSessionId = Guid.NewGuid().ToString();
            var sessionTimeout = TimeSpan.FromHours(sessionTimeoutHours);

            await redisDb.StringSetAsync(existingSessionKey, newSessionId, sessionTimeout);

            Console.WriteLine($"Created session for user {loginEvent.UserId}: {newSessionId} (expires in {sessionTimeoutHours}h)");

            await PublishSessionEventAsync(channel, "session.created", loginEvent.UserId, newSessionId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing login event: {ex.Message}");
        }
    };

    // Start consuming messages from the queue
    await channel.BasicConsumeAsync(
        queue: queueName,
        autoAck: true,
        consumer: consumer);

    Console.WriteLine("SessionService is running. Listening for login events...");
    Console.WriteLine($"Session timeout: {sessionTimeoutHours} hour(s)");
    Console.WriteLine($"Ready to receive messages on queue: {queueName}");

    // Create message consumer for presence events
    var presenceConsumer = new AsyncEventingBasicConsumer(channel);

    presenceConsumer.ReceivedAsync += async (model, ea) =>
    {
        try
        {
            var body = ea.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);
            var presenceEvent = JsonSerializer.Deserialize<PresenceEvent>(message);

            if (presenceEvent == null || string.IsNullOrEmpty(presenceEvent.UserId))
            {
                Console.WriteLine("Invalid presence event received");
                return;
            }

            var sessionKey = $"user:{presenceEvent.UserId}:session";
            var routingKey = ea.RoutingKey;

            if (routingKey == "user.connected")
            {
                // Create new session if none exists
                var existingSession = await redisDb.StringGetAsync(sessionKey);

                if (!existingSession.HasValue)
                {
                    var newSessionId = Guid.NewGuid().ToString();
                    var sessionTimeout = TimeSpan.FromHours(sessionTimeoutHours);

                    await redisDb.StringSetAsync(sessionKey, newSessionId, sessionTimeout);

                    Console.WriteLine($"Created session for connected user {presenceEvent.UserId}: {newSessionId}");

                    await PublishSessionEventAsync(channel, "session.created", presenceEvent.UserId, newSessionId);
                }
                else
                {
                    Console.WriteLine($"User {presenceEvent.UserId} connected with existing session: {existingSession}");
                }
            }
            else if (routingKey == "user.disconnected")
            {
                // Terminate session
                var existingSession = await redisDb.StringGetAsync(sessionKey);

                if (existingSession.HasValue)
                {
                    await redisDb.KeyDeleteAsync(sessionKey);

                    Console.WriteLine($"Terminated session for disconnected user {presenceEvent.UserId}: {existingSession}");

                    await PublishSessionEventAsync(channel, "session.terminated", presenceEvent.UserId, existingSession.ToString());
                }
                else
                {
                    Console.WriteLine($"User {presenceEvent.UserId} disconnected but had no active session");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing presence event: {ex.Message}");
        }
    };

    // Start consuming presence events
    await channel.BasicConsumeAsync(
        queue: presenceQueueName,
        autoAck: true,
        consumer: presenceConsumer);

    Console.WriteLine($"Listening for presence events on queue: {presenceQueueName}");

    Console.WriteLine("SessionService is running. Press Ctrl+C to exit.");

    // Keep the worker running
    var cancellationTokenSource = new CancellationTokenSource();
    Console.CancelKeyPress += (sender, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellationTokenSource.Cancel();
    };

    cancellationTokenSource.Token.WaitHandle.WaitOne();
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Environment.Exit(1);
}

Console.WriteLine("SessionService stopped.");

async Task PublishSessionEventAsync(RabbitMQ.Client.IChannel ch, string eventType, string userId, string sessionId)
{
    try
    {
        var sessionEvent = new
        {
            eventType = eventType,
            userId = userId,
            sessionId = sessionId,
            timestamp = DateTime.UtcNow
        };

        var message = JsonSerializer.Serialize(sessionEvent);
        var body = Encoding.UTF8.GetBytes(message);

        await ch.BasicPublishAsync(
            exchange: "relay.session.events",
            routingKey: $"session.{eventType.Split('.')[1]}",
            mandatory: false,
            body: body);

        Console.WriteLine($"Published session event: {eventType} for user {userId}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to publish session event: {ex.Message}");
    }
}

class LoginEvent
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

class PresenceEvent
{
    public string UserId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
