using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Infrastructure;

/// <summary>Default <see cref="IClock"/> backed by the system UTC clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
