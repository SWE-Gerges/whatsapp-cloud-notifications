using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Whatsapp.Outbox;

public sealed class OutboxDbContext(DbContextOptions<OutboxDbContext> options) : DbContext(options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();

    public DbSet<ConversationWindow> ConversationWindows => Set<ConversationWindow>();

    public DbSet<InboundMessageRecord> InboundMessages => Set<InboundMessageRecord>();

    public DbSet<DeliveryReceipt> DeliveryReceipts => Set<DeliveryReceipt>();

    /// <summary>The stored form of <see cref="NotificationLog.TemplateParameters"/>, shared with the raw insert.</summary>
    internal static string SerializeParameters(IReadOnlyList<string> parameters) =>
        JsonSerializer.Serialize(parameters, JsonOptions);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Instants are stored as UTC date-times so every provider can compare and sort them.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcInstantConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationLog>(entity =>
        {
            entity.ToTable("NotificationLogs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();

            entity.Property(x => x.TenantId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Recipient).HasMaxLength(NotificationLog.MaxRecipientLength).IsRequired();
            entity.Property(x => x.EventKey).HasMaxLength(NotificationLog.MaxKeyLength).IsRequired();
            entity.Property(x => x.ReferenceKey).HasMaxLength(NotificationLog.MaxKeyLength).IsRequired();
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.TemplateName).HasMaxLength(NotificationLog.MaxTemplateNameLength);
            entity.Property(x => x.TemplateLanguage).HasMaxLength(NotificationLog.MaxTemplateLanguageLength);
            entity.Property(x => x.Text).HasMaxLength(NotificationLog.MaxTextLength);
            entity.Property(x => x.ProviderMessageId).HasMaxLength(200);
            entity.Property(x => x.Error).HasMaxLength(NotificationLog.MaxErrorLength);

            entity.Property(x => x.TemplateParameters)
                .HasConversion(
                    parameters => SerializeParameters(parameters),
                    json => (IReadOnlyList<string>)(JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? Array.Empty<string>()),
                    new ValueComparer<IReadOnlyList<string>>(
                        (left, right) => left!.SequenceEqual(right!),
                        list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                        list => list.ToArray()))
                .IsRequired();

            // The claim-first guard: two requests for the same key can never both create a row.
            entity.HasIndex(x => new { x.TenantId, x.Recipient, x.EventKey, x.ReferenceKey })
                .IsUnique()
                .HasDatabaseName("UX_NotificationLogs_Key");

            entity.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("IX_NotificationLogs_Due");
            entity.HasIndex(x => x.LeaseToken).HasDatabaseName("IX_NotificationLogs_LeaseToken");
            entity.HasIndex(x => x.ProviderMessageId).HasDatabaseName("IX_NotificationLogs_ProviderMessageId");
            entity.HasIndex(x => new { x.TenantId, x.CreatedAt }).HasDatabaseName("IX_NotificationLogs_Tenant");
        });

        modelBuilder.Entity<InboundMessageRecord>(entity =>
        {
            entity.ToTable("InboundMessages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.TenantId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.MessageId).HasMaxLength(InboundMessageRecord.MaxMessageIdLength).IsRequired();
            entity.Property(x => x.Sender).HasMaxLength(NotificationLog.MaxRecipientLength).IsRequired();
            entity.Property(x => x.Type).HasMaxLength(InboundMessageRecord.MaxTypeLength).IsRequired();

            // A redelivered webhook carries the same message id; the unique index is what makes it a no-op.
            entity.HasIndex(x => new { x.TenantId, x.MessageId }).IsUnique().HasDatabaseName("UX_InboundMessages_Message");
        });

        modelBuilder.Entity<DeliveryReceipt>(entity =>
        {
            entity.ToTable("DeliveryReceipts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.TenantId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ProviderMessageId).HasMaxLength(DeliveryReceipt.MaxProviderMessageIdLength).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(DeliveryReceipt.MaxStatusLength).IsRequired();
            entity.Property(x => x.ErrorTitle).HasMaxLength(DeliveryReceipt.MaxErrorTitleLength);

            // The same event delivered again is recognised by this key and appends nothing.
            entity.HasIndex(x => new { x.ProviderMessageId, x.Status, x.OccurredAt }).IsUnique().HasDatabaseName("UX_DeliveryReceipts_Event");
            entity.HasIndex(x => x.NotificationId).HasDatabaseName("IX_DeliveryReceipts_NotificationId");
        });

        modelBuilder.Entity<ConversationWindow>(entity =>
        {
            entity.ToTable("ConversationWindows");
            entity.HasKey(x => new { x.TenantId, x.Recipient });
            entity.Property(x => x.TenantId).HasMaxLength(100);
            entity.Property(x => x.Recipient).HasMaxLength(NotificationLog.MaxRecipientLength);
        });
    }

    private sealed class UtcInstantConverter() : ValueConverter<DateTimeOffset, DateTime>(
        value => value.UtcDateTime,
        value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
}
