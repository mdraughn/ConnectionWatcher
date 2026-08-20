using ConPersistence;
using ConPersistence.Dto;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages.Config;

public class ProfilesModel(ConfigStore store) : PageModel
{
    public ConfigDocument Config { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Config = await store.ExportAsync();
    }
}
