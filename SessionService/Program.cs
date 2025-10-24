using RabbitMQ.Client;

Console.WriteLine($"SessionService v{SessionService.ServiceVersion.Current} starting...");

var rabbitMqHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";

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
    Console.WriteLine("Declared exchange: players (direct)");
    
    // Declare exchange: relay.session.events (topic)
    await channel.ExchangeDeclareAsync(
        exchange: "relay.session.events",
        type: ExchangeType.Topic,
        durable: true,
        autoDelete: false);
    Console.WriteLine("Declared exchange: relay.session.events (topic, durable)");

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