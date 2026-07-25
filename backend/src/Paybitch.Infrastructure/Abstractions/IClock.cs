namespace Paybitch.Infrastructure.Abstractions;

/// <summary>Injectable clock so time-dependent logic stays testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
