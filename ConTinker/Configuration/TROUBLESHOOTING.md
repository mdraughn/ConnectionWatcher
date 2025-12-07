# Troubleshooting Configuration Loading

## Issue: appsettings.Development.json Not Being Loaded

If you're experiencing issues where your `appsettings.Development.json` settings are not being applied, follow these troubleshooting steps:

### 1. Check the Environment Variable

The application needs to know it's running in Development mode. Check that the environment is set correctly:

**For Visual Studio:**
- Right-click the ConTinker project ? Properties
- Go to Debug ? General ? Open debug launch profiles UI
- Verify that `DOTNET_ENVIRONMENT` is set to `Development`
- Alternatively, check `ConTinker\Properties\launchSettings.json`

**For Command Line:**
```powershell
# PowerShell
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project ConTinker\ConTinker.csproj

# Or set it inline
dotnet run --project ConTinker\ConTinker.csproj --environment Development
```

```bash
# Bash
export DOTNET_ENVIRONMENT=Development
dotnet run --project ConTinker/ConTinker.csproj
```

### 2. Verify Configuration Files Exist

Ensure both configuration files are in the correct location and being copied to output:

```
ConTinker\
  ??? appsettings.json
  ??? appsettings.Development.json
```

Check the `.csproj` file includes these entries:
```xml
<ItemGroup>
  <Content Include="appsettings.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
  <Content Include="appsettings.Development.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

### 3. Check Console Output

When the application starts, it will log the environment and configuration:

```
[INF] Running in environment: Development
[INF] Application name: ConTinker
[INF] Content root: C:\...\ConnectionWatcher\ConTinker
[INF] Starting with HostTracking configuration: HistorySize=20, MinUsableSample=5
[INF] PingTransceiver configuration: UseSimulation=True, Preset=Unreliable, ShowControlWindow=True
```

**Look for:**
- **Environment name**: Should be "Development" (not "Production")
- **HistorySize**: Should be `20` (from Development) not `100` (from base settings)
- **UseSimulation**: Should be `True` (from Development) not `False` (from base settings)

### 4. Configuration Loading Order

`Host.CreateDefaultBuilder()` loads configuration in this order (later sources override earlier ones):

1. `appsettings.json`
2. `appsettings.{Environment}.json` (e.g., `appsettings.Development.json`)
3. User secrets (Development environment only)
4. Environment variables
5. Command-line arguments

If Development settings aren't applying, verify:
- The environment is correctly set to "Development"
- The `appsettings.Development.json` file is in the output directory
- There are no syntax errors in the JSON file

### 5. Common Issues

#### Issue: Settings from base appsettings.json being used instead of Development

**Symptom:** 
```
[INF] PingTransceiver configuration: UseSimulation=False, Preset=Stable, ShowControlWindow=False
```

**Solution:**
- The environment is not set to "Development"
- Set `DOTNET_ENVIRONMENT=Development` before running
- Use the launch profile in Visual Studio

#### Issue: Configuration validation error on startup

**Symptom:**
```
[FTL] Configuration validation failed: SimulationPreset must be one of: Perfect, Stable, Unreliable, Custom
```

**Solution:**
- Check for typos in the `SimulationPreset` value (case-insensitive but must be exact)
- Valid values: `"Perfect"`, `"Stable"`, `"Unreliable"`, `"Custom"`

#### Issue: JSON parsing error

**Symptom:**
```
Unhandled exception. System.Text.Json.JsonException: ...
```

**Solution:**
- Validate your JSON syntax using a JSON validator
- Common issues:
  - Missing commas between properties
  - Trailing commas in objects or arrays
  - Mismatched quotes or brackets
  - Invalid property names

### 6. Manual Environment Override

You can also set the environment programmatically in `Program.cs` (not recommended for production):

```csharp
IHostBuilder hostBuilder = Host.CreateDefaultBuilder();

// Force Development environment (for testing only)
hostBuilder.UseEnvironment("Development");

hostBuilder.ConfigureServices((context, services) => 
{
    // ...
});
```

### 7. Verify in Debugger

Set a breakpoint in `ConfiguredPingTransceiverFactory` constructor and inspect:
- `options.Value.UseSimulation` - should be `true` in Development
- `options.Value.ShowControlWindow` - should be `true` in Development
- `options.Value.SimulationPreset` - should be `"Unreliable"` in Development

### 8. Expected Behavior

**In Production (or when DOTNET_ENVIRONMENT is not set):**
- Uses `appsettings.json` only
- `UseSimulation = false` (real ICMP pinging)
- No control window

**In Development:**
- Uses `appsettings.json` + `appsettings.Development.json`
- `UseSimulation = true` (simulated pinging)
- `ShowControlWindow = true` (interactive UI visible)
- `SimulationPreset = "Unreliable"` (frequent issues for testing)

### 9. Quick Test

To quickly test if Development settings are loading, temporarily add this to `appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Trace"
    }
  }
}
```

If you see much more verbose logging, the Development settings are being loaded correctly.

## Need More Help?

If you're still having issues:

1. Check the log output for environment information
2. Verify the files are in the output directory (`bin\Debug\net10.0-windows\`)
3. Try deleting the `bin` and `obj` directories and rebuilding
4. Check Visual Studio's launch profile settings
5. Review the launch settings JSON file at `ConTinker\Properties\launchSettings.json`
