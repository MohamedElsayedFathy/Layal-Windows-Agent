using Layal.Models;

namespace Layal.Services;

public sealed class TaskHistoryService
{
    private readonly ProtectedJsonStore<List<AgentTaskRecord>> _store;
    private readonly object _sync = new();

    public TaskHistoryService(string path) => _store = new ProtectedJsonStore<List<AgentTaskRecord>>(path);

    public IReadOnlyList<AgentTaskRecord> GetRecent(int count = 30)
    {
        lock (_sync) return _store.Load(new List<AgentTaskRecord>()).OrderByDescending(t => t.UpdatedAt).Take(Math.Clamp(count, 1, 100)).ToList();
    }

    public void Upsert(AgentTaskRecord item)
    {
        lock (_sync)
        {
            var items = _store.Load(new List<AgentTaskRecord>());
            items.RemoveAll(t => t.Id == item.Id);
            item.UpdatedAt = DateTimeOffset.Now;
            items.Add(item);
            if (items.Count > 100) items = items.OrderByDescending(t => t.UpdatedAt).Take(100).ToList();
            _store.Save(items);
        }
    }
}
