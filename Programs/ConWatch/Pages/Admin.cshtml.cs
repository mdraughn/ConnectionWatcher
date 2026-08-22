using ConPersistence;
using ConPersistence.Dto;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

public class AdminModel(ConfigStore store, SettingsStore settingsStore) : PageModel
{
    public Dictionary<string, string> Settings { get; private set; } = new();
    public List<PoolDto> Pools { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Settings = await settingsStore.GetAllAsync();
        Pools = (await store.ExportAsync()).Pools;
    }

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var value) ? value : fallback;
}
