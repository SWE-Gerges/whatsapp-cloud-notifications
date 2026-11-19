using System.Diagnostics.Metrics;

namespace Whatsapp.Tests.Support;

/// <summary>A meter factory owned by one test, so that instruments of tests running in parallel do not mix.</summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
        lock (_meters)
        {
            _meters.Add(meter);
        }

        return meter;
    }

    public void Dispose()
    {
        lock (_meters)
        {
            foreach (var meter in _meters)
            {
                meter.Dispose();
            }

            _meters.Clear();
        }
    }
}

/// <summary>Collects the counter measurements of every meter created by one meter factory.</summary>
internal sealed class MetricRecorder : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<(string Instrument, long Value, Dictionary<string, object?> Tags)> _measurements = [];
    private readonly MeterListener _listener = new();

    public MetricRecorder(IMeterFactory scope)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, scope))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            lock (_gate)
            {
                _measurements.Add((instrument.Name, value, copy));
            }
        });
        _listener.Start();
    }

    /// <summary>The sum of an instrument's measurements whose tags include every given pair.</summary>
    public long Sum(string instrument, params (string Key, string Value)[] tags)
    {
        lock (_gate)
        {
            return _measurements
                .Where(m => m.Instrument == instrument && tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && Equals(v, t.Value)))
                .Sum(m => m.Value);
        }
    }

    public void Dispose() => _listener.Dispose();
}

internal static class Fixtures
{
    /// <summary>Reads a webhook fixture, replacing each key of <paramref name="replacements"/> by its value.</summary>
    public static string Webhook(string name, params (string Old, string New)[] replacements)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Webhooks", "Fixtures", name));
        foreach (var (old, replacement) in replacements)
        {
            if (!text.Contains(old, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The fixture {name} does not contain '{old}'.");
            }

            text = text.Replace(old, replacement, StringComparison.Ordinal);
        }

        return text;
    }
}
