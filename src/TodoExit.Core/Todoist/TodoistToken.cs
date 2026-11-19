namespace TodoExit.Core.Todoist;

/// <summary>A personal Todoist API token. Kept in memory only; <see cref="ToString"/> never shows it.</summary>
public sealed class TodoistToken
{
    private readonly string _value;

    public TodoistToken(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var v = value.Trim();
        if (v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            v = v[7..].Trim();
        }

        if (v.Length == 0)
        {
            throw new ArgumentException("The Todoist token is empty.", nameof(value));
        }

        _value = v;
    }

    /// <summary>Only the client reads the value, to build the Authorization header.</summary>
    internal string Value => _value;

    public override string ToString() => "TodoistToken(hidden)";
}
