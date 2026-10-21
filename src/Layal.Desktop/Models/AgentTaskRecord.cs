namespace Layal.Models;

public sealed class AgentTaskRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Goal { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string Result { get; set; } = string.Empty;
    public List<string> Events { get; set; } = new();
}
