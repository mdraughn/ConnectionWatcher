using ConPersistence.Settings;

namespace ConWatch.Pages;

/// <summary>Admin-locked by convention (AdminLockPageFilter in ConWatchServices).</summary>
public class DataModel(SettingsStore settingsStore) : SettingsPageModel(settingsStore);
