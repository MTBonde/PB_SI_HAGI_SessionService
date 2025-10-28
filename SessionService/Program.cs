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
