using System.ComponentModel.DataAnnotations;

namespace Whatsapp.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Whatsapp:Outbox";

    /// <summary>Turn the background dispatcher off, for example in tests that drive the processor directly.</summary>
    public bool DispatcherEnabled { get; set; } = true;

    /// <summary>How the database schema is prepared when the host starts.</summary>
    public OutboxSchemaMode SchemaMode { get; set; } = OutboxSchemaMode.Auto;

    /// <summary>Pause between polls when the last batch was not full.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    [Range(1, 500)]
    public int BatchSize { get; set; } = 20;

    /// <summary>Maximum number of rows sent in parallel by one dispatcher.</summary>
    [Range(1, 64)]
    public int Concurrency { get; set; } = 4;

    /// <summary>Total send attempts (including the first) before a row is dead-lettered.</summary>
    [Range(1, 50)]
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long a claimed row stays reserved. Must exceed the longest possible send.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Delay before the first retry. Later retries double it.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromHours(1);
}

public enum OutboxSchemaMode
{
    /// <summary>Migrate on PostgreSQL, where the committed migrations apply, and create the schema from the model on any other provider.</summary>
    Auto = 0,

    /// <summary>Apply the committed EF Core migrations, creating the database first when it does not exist (PostgreSQL only).</summary>
    Migrate = 1,

    /// <summary>Create the schema from the current model without migrations. For SQLite development and tests.</summary>
    EnsureCreated = 2,

    /// <summary>Leave the schema alone; something else manages it.</summary>
    None = 3,
}

public sealed class ConversationWindowOptions
{
    public const string SectionName = "Whatsapp:ConversationWindow";

    /// <summary>Length of the service window opened by an inbound customer message.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromHours(24);
}
