using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WindowsGSH.Core.Readiness;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;

namespace WindowsGSH.Modules.Windrose;

public sealed class WindroseModule :
    IGameServerModule,
    IManifestBackedModule,
    IModuleExistingServerImportCapability,
    IModuleReadinessCapability
{
    private const string WindrosePlusAddonId = "windrose-plus";
    private const string ServerDescriptionRelativePath = @"R5\ServerDescription.json";
    private const string SaveProfileRelativePath = @"R5\Saved\SaveProfiles\Default";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private ModuleManifest? _manifest;
    private string _moduleDirectory = AppContext.BaseDirectory;

    private ModuleManifest Manifest => _manifest ??= ModuleManifest.Load(Path.Combine(_moduleDirectory, "module.json"));

    public string Id => Manifest.Id;

    public string Name => Manifest.Name;

    public string Version => Manifest.Version;

    public ModuleCapabilities Capabilities => Manifest.ToCapabilities(supportsQuery: true, supportsRcon: false);

    public SteamInstallDefinition? SteamInstall => Manifest.ToSteamInstall();

    public ModuleRuntimeDefinition Runtime => Manifest.ToRuntime();

    public void Configure(ModuleManifest manifest, string moduleDirectory)
    {
        _manifest = manifest;
        _moduleDirectory = moduleDirectory;
    }

    public IReadOnlyList<ConfigFieldDefinition> GetConfigFields()
    {
        return Manifest.ToConfigFields();
    }

    public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions()
    {
        return Manifest.ToAddons();
    }

    public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets()
    {
        return Manifest.ToBackupTargets();
    }

    public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId)
    {
        if (!string.Equals(addonId, WindrosePlusAddonId, StringComparison.OrdinalIgnoreCase))
        {
            return new ServerAddonStatus(addonId, IsInstalled: false, IsEnabled: false, StatusText: "Unknown addon");
        }

        var enabled = IsAddonEnabled(instance.ConfigPath, WindrosePlusAddonId);
        var installed = IsWindrosePlusInstalled(instance);
        var status = (installed, enabled) switch
        {
            (true, true) => "Installed and enabled",
            (true, false) => "Installed, disabled",
            (false, true) => "Enabled, install required",
            _ => "Not installed"
        };

        return new ServerAddonStatus(WindrosePlusAddonId, installed, enabled, status);
    }

    public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken)
    {
        if (!string.Equals(addonId, WindrosePlusAddonId, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Unknown addon: {addonId}");
        }

        UpdateAddonConfig(instance.ConfigPath, WindrosePlusAddonId, enabled: true);
        UpdateWindrosePlusConfig(instance);
        return Task.CompletedTask;
    }

    public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken)
    {
        if (!string.Equals(addonId, WindrosePlusAddonId, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Unknown addon: {addonId}");
        }

        UpdateAddonConfig(instance.ConfigPath, WindrosePlusAddonId, enabled: false);
        return Task.CompletedTask;
    }

    public string GetServerName(IReadOnlyDictionary<string, object?> settings)
    {
        return GetSetting(settings, "server.name", "Windrose Server");
    }

    public ServerDisplayInfo GetDisplayInfo(ServerInstance instance)
    {
        return new ServerDisplayInfo(
            IpAddress: GetSetting(instance, "network.proxyAddress", "0.0.0.0"),
            Port: GetSetting(instance, "network.directConnectionPort", "7777"),
            MaxPlayers: GetSetting(instance, "server.maxPlayers", "4"));
    }

    public Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var settings = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        ReadServerDescription(instance, settings);
        ReadWorldDescription(instance, settings);

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(settings);
    }

    public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        WriteServerDescription(instance);
        WriteWorldDescriptionIfAvailable(instance);
        return Task.CompletedTask;
    }

    public bool CanImport(string path)
    {
        return ResolveImportInstallPath(path) != null;
    }

    public async Task<ModuleExistingServerImportProbe> PreviewImportAsync(string path, CancellationToken cancellationToken)
    {
        var installPath = ResolveImportInstallPath(path) ?? path;
        var settings = GetConfigFields().ToDictionary(
            field => field.Key,
            field => field.DefaultValue,
            StringComparer.OrdinalIgnoreCase);

        var probe = new ServerInstance(
            "windrose-import",
            Path.GetFileName(installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Id,
            installPath,
            installPath,
            Path.Combine(installPath, ServerDescriptionRelativePath),
            settings);

        foreach (var pair in await ReadConfigFileSettingsAsync(probe, cancellationToken).ConfigureAwait(false))
        {
            settings[pair.Key] = pair.Value;
        }

        var warnings = new List<string>();
        if (!File.Exists(Path.Combine(installPath, ServerDescriptionRelativePath)))
        {
            warnings.Add("R5/ServerDescription.json was not found. Windrose usually generates it on first launch; WindowsGSH will create one from module settings before start.");
        }

        if (!TryResolveWorldDescriptionPath(installPath, GetSetting(settings, "server.worldIslandId", ""), out _))
        {
            warnings.Add("WorldDescription.json was not found yet. World settings will be available after Windrose creates a world save folder.");
        }

        return new ModuleExistingServerImportProbe(
            GetSetting(settings, "server.name", Path.GetFileName(installPath)),
            installPath,
            settings,
            warnings);
    }

    public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var plan = new InstallPlan(
            Tool: "steamcmd",
            Arguments: $"+force_install_dir \"{instance.InstallPath}\" +login anonymous +app_update {SteamInstall?.AppId} validate +quit",
            WorkingDirectory: instance.InstallPath,
            Notes:
            [
                "Windrose Dedicated Server is available anonymously through SteamCMD.",
                "Keep the dedicated server version matched with the client version."
            ]);

        return Task.FromResult(plan);
    }

    public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var serverExecutable = Path.Combine(instance.InstallPath, Runtime.StartPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = serverExecutable,
            WorkingDirectory = instance.InstallPath,
            Arguments = BuildLaunchArguments(instance),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        return Task.FromResult(startInfo);
    }

    public async Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        if (!IsInstallValid(instance))
        {
            throw new FileNotFoundException("Windrose server executable was not found.", Path.Combine(instance.InstallPath, Runtime.StartPath));
        }

        if (IsWindrosePlusEnabled(instance) && IsWindrosePlusInstalled(instance))
        {
            await BuildWindrosePlusPakAsync(instance, cancellationToken);
        }

        var startInfo = await CreateStartInfoAsync(instance, cancellationToken);
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.Start();
        return process;
    }

    public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        if (!IsWindrosePlusEnabled(instance) || !IsWindrosePlusInstalled(instance))
        {
            return Task.FromResult<IReadOnlyList<Process>>([]);
        }

        var dashboardBat = Path.Combine(instance.InstallPath, "windrose_plus", "start_dashboard.bat");
        if (!File.Exists(dashboardBat))
        {
            return Task.FromResult<IReadOnlyList<Process>>([]);
        }

        var process = new Process
        {
            StartInfo = CreateHiddenCommandStartInfo($"/c \"{dashboardBat}\"", instance.InstallPath),
            EnableRaisingEvents = true
        };

        process.Start();
        return Task.FromResult<IReadOnlyList<Process>>([process]);
    }

    public async Task StopAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var processes = ServerProcessLocator.FindProcesses(this, instance.InstallPath);
        foreach (var process in processes)
        {
            using (process)
            {
                if (process.HasExited)
                {
                    continue;
                }

                try
                {
                    process.CloseMainWindow();
                    await Task.Delay(1500, cancellationToken);
                }
                catch
                {
                }

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
                }
            }
        }
    }

    public bool IsInstallValid(ServerInstance instance)
    {
        return File.Exists(Path.Combine(instance.InstallPath, Runtime.StartPath));
    }

    public Task<IReadOnlyList<ReadinessCheckResult>> CheckReadinessAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var checks = new List<ReadinessCheckResult>();
        var executablePath = Path.Combine(instance.InstallPath, Runtime.StartPath);
        checks.Add(File.Exists(executablePath)
            ? ReadinessCheckResult.Pass("Windrose executable", $"Found: {executablePath}")
            : ReadinessCheckResult.Fail("Windrose executable", $"Missing {Runtime.StartPath}. Run install/update with SteamCMD app {SteamInstall?.AppId}."));

        var serverDescriptionPath = Path.Combine(instance.InstallPath, ServerDescriptionRelativePath);
        checks.Add(File.Exists(serverDescriptionPath)
            ? ReadinessCheckResult.Pass("Server description", $"Found: {serverDescriptionPath}")
            : ReadinessCheckResult.Info("Server description", "R5/ServerDescription.json will be created before the server starts."));

        var worldIslandId = GetSetting(instance, "server.worldIslandId", "");
        if (TryResolveWorldDescriptionPath(instance.InstallPath, worldIslandId, out var worldPath))
        {
            checks.Add(ReadinessCheckResult.Pass("World description", $"Found: {worldPath}"));
            var updaterPath = Path.Combine(instance.InstallPath, "R5WorldDescriptionUpdater.exe");
            checks.Add(File.Exists(updaterPath)
                ? ReadinessCheckResult.Pass("World updater", $"Found: {updaterPath}")
                : ReadinessCheckResult.Warning("World updater", "R5WorldDescriptionUpdater.exe was not found. WorldDescription.json changes may not be applied by Windrose until the updater is available."));
        }
        else
        {
            checks.Add(ReadinessCheckResult.Info(
                "World description",
                "WorldDescription.json is generated by Windrose after a world exists. WindowsGSH will apply world settings once it can find a matching island id."));
        }

        return Task.FromResult<IReadOnlyList<ReadinessCheckResult>>(checks);
    }

    public string? GetConsoleLogPath(ServerInstance instance)
    {
        return null;
    }

    public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken)
    {
        if (!IsAddonEnabled(instance.ConfigPath, WindrosePlusAddonId))
        {
            throw new NotSupportedException("Windrose RCON requires the WindrosePlus addon.");
        }

        return ExecuteWindrosePlusRconAsync(instance, command, cancellationToken);
    }

    public async Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        if (!GetBool(instance, "network.useDirectConnection"))
        {
            var isRunning = ServerProcessLocator.IsRunning(this, instance.InstallPath);
            if (TryReadWindrosePlusStatus(instance, out var addonResult))
            {
                return addonResult;
            }

            return new QueryResult(
                isRunning ? ModuleServerStatus.Online : ModuleServerStatus.Offline,
                MaxPlayers: ParseInt(GetSetting(instance, "server.maxPlayers", "4"), 4),
                Message: isRunning
                    ? "P2P mode is running. Direct port checks are only attempted for direct-connection mode."
                    : "P2P mode process is not running.");
        }

        var host = GetConnectableHost(GetSetting(instance, "network.proxyAddress", "127.0.0.1"));
        var port = ParseInt(GetSetting(instance, "network.directConnectionPort", "7777"), 7777);

        try
        {
            await CheckTcpPortAsync(host, port, TimeSpan.FromSeconds(2), cancellationToken);
            if (TryReadWindrosePlusStatus(instance, out var addonResult))
            {
                return addonResult with
                {
                    Message = $"Direct connection port responded on {host}:{port}. {addonResult.Message}"
                };
            }

            return new QueryResult(
                ModuleServerStatus.Online,
                MaxPlayers: ParseInt(GetSetting(instance, "server.maxPlayers", "4"), 4),
                Message: $"Direct connection port responded on {host}:{port}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new QueryResult(
                ModuleServerStatus.Offline,
                MaxPlayers: ParseInt(GetSetting(instance, "server.maxPlayers", "4"), 4),
                Message: $"Direct connection check to {host}:{port} timed out.");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return new QueryResult(
                ModuleServerStatus.Offline,
                MaxPlayers: ParseInt(GetSetting(instance, "server.maxPlayers", "4"), 4),
                Message: $"Direct connection check to {host}:{port} failed: {ex.Message}");
        }
    }

    private string BuildLaunchArguments(ServerInstance instance)
    {
        var arguments = Regex.Replace(Manifest.GetDefaultArguments(), "\\{(?<key>[^}]+)\\}", match =>
        {
            var key = match.Groups["key"].Value;
            return instance.Settings.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
        });

        var additional = GetSetting(instance, "server.additionalArguments", "");
        if (!string.IsNullOrWhiteSpace(additional))
        {
            arguments = string.IsNullOrWhiteSpace(arguments)
                ? additional.Trim()
                : $"{arguments} {additional.Trim()}";
        }

        return arguments;
    }

    private static async Task BuildWindrosePlusPakAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var buildScript = Path.Combine(instance.InstallPath, "windrose_plus", "tools", "WindrosePlus-BuildPak.ps1");
        if (!File.Exists(buildScript))
        {
            return;
        }

        await RunPowerShellAsync(
            instance.InstallPath,
            $"-NoProfile -ExecutionPolicy Bypass -File \"{buildScript}\" -ServerDir \"{instance.InstallPath}\" -RemoveStalePak",
            instance.Id,
            cancellationToken);
    }

    private static async Task RunPowerShellAsync(string workingDirectory, string arguments, string source, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = workingDirectory,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;

        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            _ = line;
        }

        foreach (var line in error.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            _ = line;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"PowerShell command failed with exit code {process.ExitCode}.");
        }
    }

    private static ProcessStartInfo CreateHiddenCommandStartInfo(string arguments, string workingDirectory)
    {
        return new ProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = workingDirectory,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static bool IsWindrosePlusEnabled(ServerInstance instance)
    {
        return IsAddonEnabled(instance.ConfigPath, WindrosePlusAddonId);
    }

    private static bool IsWindrosePlusInstalled(ServerInstance instance)
    {
        return File.Exists(Path.Combine(instance.InstallPath, "windrose_plus", "start_dashboard.bat")) ||
               File.Exists(Path.Combine(instance.InstallPath, "UE4SS-settings.ini"));
    }

    private static bool IsAddonEnabled(string configPath, string addonId)
    {
        var config = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject();
        if (config?["addons"] is not JsonObject addons ||
            addons[addonId] is not JsonObject addon ||
            addon["enabled"] is not JsonValue enabledValue ||
            !enabledValue.TryGetValue<bool>(out var enabled))
        {
            return false;
        }

        return enabled;
    }

    private static void UpdateAddonConfig(string configPath, string addonId, bool enabled)
    {
        var config = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() ?? [];
        var addons = config["addons"] as JsonObject ?? [];
        config["addons"] = addons;

        var addon = addons[addonId] as JsonObject ?? [];
        addons[addonId] = addon;
        addon["enabled"] = enabled;

        var settings = addon["settings"] as JsonObject ?? [];
        addon["settings"] = settings;
        settings["rcon.enabled"] ??= false;
        settings["rcon.password"] ??= "change-me";
        settings["query.intervalMs"] ??= 5000;
        settings["query.idleIntervalMs"] ??= 30000;

        File.WriteAllText(configPath, config.ToJsonString(JsonOptions), Utf8NoBom);
    }

    private static void UpdateWindrosePlusConfig(ServerInstance instance)
    {
        if (!IsWindrosePlusInstalled(instance))
        {
            return;
        }

        var path = Path.Combine(instance.InstallPath, "windrose_plus.json");
        var config = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? []
            : [];

        var rcon = config["rcon"] as JsonObject ?? [];
        config["rcon"] = rcon;
        rcon["enabled"] = GetAddonBoolSetting(instance.ConfigPath, WindrosePlusAddonId, "rcon.enabled", true);
        rcon["password"] = GetAddonSetting(instance.ConfigPath, WindrosePlusAddonId, "rcon.password", "change-me");

        var server = config["server"] as JsonObject ?? [];
        config["server"] = server;
        server["query_interval_ms"] = GetAddonIntSetting(instance.ConfigPath, WindrosePlusAddonId, "query.intervalMs", 5000);
        server["query_idle_interval_ms"] = GetAddonIntSetting(instance.ConfigPath, WindrosePlusAddonId, "query.idleIntervalMs", 30000);

        File.WriteAllText(path, config.ToJsonString(JsonOptions), Utf8NoBom);
    }

    private static bool TryReadWindrosePlusStatus(ServerInstance instance, out QueryResult queryResult)
    {
        queryResult = new QueryResult(ModuleServerStatus.Unknown);
        if (!IsAddonEnabled(instance.ConfigPath, WindrosePlusAddonId))
        {
            return false;
        }

        var path = Path.Combine(instance.InstallPath, "windrose_plus_data", "server_status.json");
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var server = document.RootElement.TryGetProperty("server", out var foundServer)
                ? foundServer
                : default;
            var players = GetJsonInt(server, "player_count");
            var maxPlayers = GetJsonInt(server, "max_players") ?? ParseInt(GetSetting(instance, "server.maxPlayers", "4"), 4);
            var version = GetJsonString(server, "version");
            queryResult = new QueryResult(
                ModuleServerStatus.Online,
                OnlinePlayers: players,
                MaxPlayers: maxPlayers,
                Version: version,
                Message: "WindrosePlus status file loaded.");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ExecuteWindrosePlusRconAsync(ServerInstance instance, string command, CancellationToken cancellationToken)
    {
        var password = GetAddonSetting(instance.ConfigPath, WindrosePlusAddonId, "rcon.password", "");
        if (string.IsNullOrWhiteSpace(password) || string.Equals(password, "change-me", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("WindrosePlus RCON password is not configured.");
        }

        var spoolDir = Path.Combine(instance.InstallPath, "windrose_plus_data", "rcon");
        Directory.CreateDirectory(spoolDir);

        var id = "wingsh_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "_" + Random.Shared.Next(100000, 999999);
        var cmdPath = Path.Combine(spoolDir, $"cmd_{id}.json");
        var resPath = Path.Combine(spoolDir, $"res_{id}.json");
        var payload = new JsonObject
        {
            ["id"] = id,
            ["command"] = command,
            ["password"] = password,
            ["admin_user"] = "WindowsGSH",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        await File.WriteAllTextAsync(cmdPath, payload.ToJsonString(), cancellationToken);
        await File.AppendAllTextAsync(Path.Combine(spoolDir, "pending_commands.txt"), $"cmd_{id}.json\r\n", cancellationToken);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(resPath))
            {
                var response = await File.ReadAllTextAsync(resPath, cancellationToken);
                File.Delete(resPath);
                using var document = JsonDocument.Parse(response);
                var status = GetJsonString(document.RootElement, "status") ?? "unknown";
                var message = GetJsonString(document.RootElement, "message") ?? response;
                return $"{status}: {message}";
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException("WindrosePlus RCON did not return a response.");
    }

    private static string GetAddonSetting(string configPath, string addonId, string key, string fallback)
    {
        var config = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject();
        if (config?["addons"] is not JsonObject addons ||
            addons[addonId] is not JsonObject addon ||
            addon["settings"] is not JsonObject settings ||
            settings[key] is not JsonValue value ||
            !value.TryGetValue<string>(out var text) ||
            string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return text;
    }

    private static bool GetAddonBoolSetting(string configPath, string addonId, string key, bool fallback)
    {
        var config = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject();
        if (config?["addons"] is not JsonObject addons ||
            addons[addonId] is not JsonObject addon ||
            addon["settings"] is not JsonObject settings ||
            settings[key] is not JsonValue value ||
            !value.TryGetValue<bool>(out var result))
        {
            return fallback;
        }

        return result;
    }

    private static int GetAddonIntSetting(string configPath, string addonId, string key, int fallback)
    {
        var config = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject();
        if (config?["addons"] is not JsonObject addons ||
            addons[addonId] is not JsonObject addon ||
            addon["settings"] is not JsonObject settings ||
            settings[key] is not JsonValue value ||
            !value.TryGetValue<int>(out var result))
        {
            return fallback;
        }

        return result;
    }

    private static int? GetJsonInt(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var value) &&
               value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static void ReadServerDescription(ServerInstance instance, Dictionary<string, object?> settings)
    {
        var path = Path.Combine(instance.InstallPath, ServerDescriptionRelativePath);
        if (!File.Exists(path))
        {
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("ServerDescription_Persistent", out var persistent) ||
            persistent.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        SetString(settings, "server.name", persistent, "ServerName", allowEmpty: false);
        SetString(settings, "server.inviteCode", persistent, "InviteCode", allowEmpty: false);
        SetBool(settings, "server.passwordProtected", persistent, "IsPasswordProtected");
        SetString(settings, "server.password", persistent, "Password", allowEmpty: true);
        SetInt(settings, "server.maxPlayers", persistent, "MaxPlayerCount");
        SetRegion(settings, persistent);
        SetString(settings, "server.worldIslandId", persistent, "WorldIslandId", allowEmpty: false);
        SetBool(settings, "network.useDirectConnection", persistent, "UseDirectConnection");
        SetPort(settings, "network.directConnectionPort", persistent, "DirectConnectionServerPort");
        SetString(settings, "network.proxyAddress", persistent, "DirectConnectionProxyAddress", allowEmpty: false);
        SetBool(settings, "recovery.autoLoadLatestBackup", persistent, "AutoLoadLatestBackupIfHasBroken");
    }

    private static void ReadWorldDescription(ServerInstance instance, Dictionary<string, object?> settings)
    {
        var worldIslandId = GetSetting(settings, "server.worldIslandId", "");
        if (!TryResolveWorldDescriptionPath(instance.InstallPath, worldIslandId, out var path))
        {
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("WorldDescription", out var world) ||
            world.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        SetString(settings, "server.worldIslandId", world, "islandId", allowEmpty: false);
        SetString(settings, "world.name", world, "WorldName", allowEmpty: true);
        SetString(settings, "world.presetType", world, "WorldPresetType", allowEmpty: false);

        if (world.TryGetProperty("WorldSettings", out var worldSettings) &&
            worldSettings.ValueKind == JsonValueKind.Object)
        {
            ReadBoolParameter(settings, worldSettings, "world.sharedQuests", "WDS.Parameter.Coop.SharedQuests");
            ReadBoolParameter(settings, worldSettings, "world.easyExplore", "WDS.Parameter.EasyExplore");
            ReadFloatParameter(settings, worldSettings, "world.mobHealthMultiplier", "WDS.Parameter.MobHealthMultiplier");
            ReadFloatParameter(settings, worldSettings, "world.mobDamageMultiplier", "WDS.Parameter.MobDamageMultiplier");
            ReadFloatParameter(settings, worldSettings, "world.shipsHealthMultiplier", "WDS.Parameter.ShipsHealthMultiplier");
            ReadFloatParameter(settings, worldSettings, "world.shipsDamageMultiplier", "WDS.Parameter.ShipsDamageMultiplier");
            ReadFloatParameter(settings, worldSettings, "world.boardingDifficultyMultiplier", "WDS.Parameter.BoardingDifficultyMultiplier");
            ReadFloatParameter(settings, worldSettings, "world.coopStatsCorrectionModifier", "WDS.Parameter.Coop.StatsCorrectionModifier");
            ReadFloatParameter(settings, worldSettings, "world.coopShipStatsCorrectionModifier", "WDS.Parameter.Coop.ShipStatsCorrectionModifier");
            ReadTagParameter(settings, worldSettings, "world.combatDifficulty", "WDS.Parameter.CombatDifficulty", "WDS.Parameter.CombatDifficulty.");
        }
    }

    private static void WriteServerDescription(ServerInstance instance)
    {
        var path = Path.Combine(instance.InstallPath, ServerDescriptionRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? []
            : [];

        var persistent = root["ServerDescription_Persistent"] as JsonObject ?? [];
        root["ServerDescription_Persistent"] = persistent;

        persistent["ServerName"] = GetSetting(instance, "server.name", "");
        persistent["InviteCode"] = GetSetting(instance, "server.inviteCode", "");
        persistent["IsPasswordProtected"] = GetBool(instance, "server.passwordProtected");
        persistent["Password"] = GetSetting(instance, "server.password", "");
        persistent["MaxPlayerCount"] = ParseInt(GetSetting(instance, "server.maxPlayers", "4"), 4);
        var region = GetSetting(instance, "server.region", "Auto");
        persistent["UserSelectedRegion"] = string.Equals(region, "Auto", StringComparison.OrdinalIgnoreCase) ? "" : region;
        persistent["WorldIslandId"] = GetSetting(instance, "server.worldIslandId", "");
        persistent["UseDirectConnection"] = GetBool(instance, "network.useDirectConnection");
        persistent["DirectConnectionServerPort"] = ParseInt(GetSetting(instance, "network.directConnectionPort", "7777"), 7777);
        persistent["DirectConnectionProxyAddress"] = GetSetting(instance, "network.proxyAddress", "0.0.0.0");
        persistent["AutoLoadLatestBackupIfHasBroken"] = GetBool(instance, "recovery.autoLoadLatestBackup");

        File.WriteAllText(path, root.ToJsonString(JsonOptions) + Environment.NewLine, Utf8NoBom);
    }

    private static void WriteWorldDescriptionIfAvailable(ServerInstance instance)
    {
        var worldIslandId = GetSetting(instance, "server.worldIslandId", "");
        if (!TryResolveWorldDescriptionPath(instance.InstallPath, worldIslandId, out var path))
        {
            return;
        }

        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [];
        var world = root["WorldDescription"] as JsonObject ?? [];
        root["WorldDescription"] = world;

        var configuredIslandId = GetSetting(instance, "server.worldIslandId", "");
        if (!string.IsNullOrWhiteSpace(configuredIslandId))
        {
            world["islandId"] = configuredIslandId;
        }

        world["WorldName"] = GetSetting(instance, "world.name", GetNodeString(world, "WorldName", ""));
        world["WorldPresetType"] = GetSetting(instance, "world.presetType", GetNodeString(world, "WorldPresetType", "Medium"));

        var worldSettings = world["WorldSettings"] as JsonObject ?? [];
        world["WorldSettings"] = worldSettings;
        WriteBoolParameter(worldSettings, instance, "world.sharedQuests", "WDS.Parameter.Coop.SharedQuests", false);
        WriteBoolParameter(worldSettings, instance, "world.easyExplore", "WDS.Parameter.EasyExplore", false);
        WriteFloatParameter(worldSettings, instance, "world.mobHealthMultiplier", "WDS.Parameter.MobHealthMultiplier", 1);
        WriteFloatParameter(worldSettings, instance, "world.mobDamageMultiplier", "WDS.Parameter.MobDamageMultiplier", 1);
        WriteFloatParameter(worldSettings, instance, "world.shipsHealthMultiplier", "WDS.Parameter.ShipsHealthMultiplier", 1);
        WriteFloatParameter(worldSettings, instance, "world.shipsDamageMultiplier", "WDS.Parameter.ShipsDamageMultiplier", 1);
        WriteFloatParameter(worldSettings, instance, "world.boardingDifficultyMultiplier", "WDS.Parameter.BoardingDifficultyMultiplier", 1);
        WriteFloatParameter(worldSettings, instance, "world.coopStatsCorrectionModifier", "WDS.Parameter.Coop.StatsCorrectionModifier", 1);
        WriteFloatParameter(worldSettings, instance, "world.coopShipStatsCorrectionModifier", "WDS.Parameter.Coop.ShipStatsCorrectionModifier", 0);
        WriteTagParameter(worldSettings, instance, "world.combatDifficulty", "WDS.Parameter.CombatDifficulty", "WDS.Parameter.CombatDifficulty.", "Normal");

        File.WriteAllText(path, root.ToJsonString(JsonOptions) + Environment.NewLine, Utf8NoBom);
        RunWorldDescriptionUpdater(instance.InstallPath, path);
    }

    private static void RunWorldDescriptionUpdater(string installPath, string worldDescriptionPath)
    {
        var updaterPath = Path.Combine(installPath, "R5WorldDescriptionUpdater.exe");
        if (!File.Exists(updaterPath))
        {
            return;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = updaterPath,
                WorkingDirectory = installPath,
                Arguments = QuoteArgument(worldDescriptionPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();
        if (!process.WaitForExit(15000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException("R5WorldDescriptionUpdater.exe did not finish within 15 seconds.");
        }

        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"R5WorldDescriptionUpdater.exe failed with exit code {process.ExitCode}. {error}");
        }
    }

    private static string? ResolveImportInstallPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        if (File.Exists(Path.Combine(fullPath, "R5", "Binaries", "Win64", "WindroseServer-Win64-Shipping.exe")))
        {
            return fullPath;
        }

        var serverFiles = Path.Combine(fullPath, "serverfiles");
        return File.Exists(Path.Combine(serverFiles, "R5", "Binaries", "Win64", "WindroseServer-Win64-Shipping.exe"))
            ? serverFiles
            : null;
    }

    private static bool TryResolveWorldDescriptionPath(string installPath, string? worldIslandId, out string path)
    {
        path = string.Empty;
        var saveRoot = Path.Combine(installPath, SaveProfileRelativePath);
        if (!Directory.Exists(saveRoot))
        {
            return false;
        }

        var files = Directory.EnumerateFiles(saveRoot, "WorldDescription.json", SearchOption.AllDirectories)
            .Select(file => new FileInfo(file))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(worldIslandId))
        {
            var matching = files.FirstOrDefault(file => WorldDescriptionMatchesIslandId(file.FullName, worldIslandId));
            if (matching != null)
            {
                path = matching.FullName;
                return true;
            }
        }

        var latest = files.FirstOrDefault();
        if (latest == null)
        {
            return false;
        }

        path = latest.FullName;
        return true;
    }

    private static bool WorldDescriptionMatchesIslandId(string path, string worldIslandId)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("WorldDescription", out var world) &&
                   world.TryGetProperty("islandId", out var islandId) &&
                   string.Equals(islandId.GetString(), worldIslandId, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetSetting(ServerInstance instance, string key, string fallback)
    {
        return GetSetting(instance.Settings, key, fallback);
    }

    private static string GetSetting(IReadOnlyDictionary<string, object?> settings, string key, string fallback)
    {
        return settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value?.ToString())
            ? value.ToString()!.Trim()
            : fallback;
    }

    private static void SetString(Dictionary<string, object?> settings, string key, JsonElement source, string propertyName, bool allowEmpty)
    {
        if (!source.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var text = value.GetString() ?? string.Empty;
        if (allowEmpty || !string.IsNullOrWhiteSpace(text))
        {
            settings[key] = text;
        }
    }

    private static void SetRegion(Dictionary<string, object?> settings, JsonElement source)
    {
        if (!source.TryGetProperty("UserSelectedRegion", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        settings["server.region"] = string.IsNullOrWhiteSpace(value.GetString()) ? "Auto" : value.GetString();
    }

    private static void SetBool(Dictionary<string, object?> settings, string key, JsonElement source, string propertyName)
    {
        if (source.TryGetProperty(propertyName, out var value) &&
            (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
        {
            settings[key] = value.GetBoolean();
        }
    }

    private static void SetInt(Dictionary<string, object?> settings, string key, JsonElement source, string propertyName)
    {
        if (source.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number))
        {
            settings[key] = number;
        }
    }

    private static void SetPort(Dictionary<string, object?> settings, string key, JsonElement source, string propertyName)
    {
        if (source.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number) && number > 0)
        {
            settings[key] = number;
        }
    }

    private static void ReadBoolParameter(Dictionary<string, object?> settings, JsonElement worldSettings, string key, string tagName)
    {
        if (TryGetParameterObject(worldSettings, "BoolParameters", out var parameters) &&
            parameters.TryGetProperty(ToTagKey(tagName), out var value) &&
            (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
        {
            settings[key] = value.GetBoolean();
        }
    }

    private static void ReadFloatParameter(Dictionary<string, object?> settings, JsonElement worldSettings, string key, string tagName)
    {
        if (TryGetParameterObject(worldSettings, "FloatParameters", out var parameters) &&
            parameters.TryGetProperty(ToTagKey(tagName), out var value) &&
            value.TryGetDouble(out var number))
        {
            settings[key] = number;
        }
    }

    private static void ReadTagParameter(Dictionary<string, object?> settings, JsonElement worldSettings, string key, string tagName, string prefix)
    {
        if (!TryGetParameterObject(worldSettings, "TagParameters", out var parameters) ||
            !parameters.TryGetProperty(ToTagKey(tagName), out var value) ||
            !value.TryGetProperty("TagName", out var selectedTag) ||
            selectedTag.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var tag = selectedTag.GetString() ?? string.Empty;
        settings[key] = tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? tag[prefix.Length..]
            : tag;
    }

    private static bool TryGetParameterObject(JsonElement worldSettings, string propertyName, out JsonElement parameters)
    {
        return worldSettings.TryGetProperty(propertyName, out parameters) &&
               parameters.ValueKind == JsonValueKind.Object;
    }

    private static void WriteBoolParameter(JsonObject worldSettings, ServerInstance instance, string key, string tagName, bool fallback)
    {
        var parameters = EnsureObject(worldSettings, "BoolParameters");
        parameters[ToTagKey(tagName)] = GetBool(instance, key, fallback);
    }

    private static void WriteFloatParameter(JsonObject worldSettings, ServerInstance instance, string key, string tagName, double fallback)
    {
        var parameters = EnsureObject(worldSettings, "FloatParameters");
        parameters[ToTagKey(tagName)] = GetDouble(instance.Settings, key, fallback);
    }

    private static void WriteTagParameter(JsonObject worldSettings, ServerInstance instance, string key, string tagName, string prefix, string fallback)
    {
        var parameters = EnsureObject(worldSettings, "TagParameters");
        var selected = GetSetting(instance, key, fallback);
        parameters[ToTagKey(tagName)] = new JsonObject
        {
            ["TagName"] = selected.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? selected : prefix + selected
        };
    }

    private static JsonObject EnsureObject(JsonObject root, string key)
    {
        var obj = root[key] as JsonObject ?? [];
        root[key] = obj;
        return obj;
    }

    private static string ToTagKey(string tagName)
    {
        return "{\"TagName\": \"" + tagName + "\"}";
    }

    private static string GetNodeString(JsonObject obj, string key, string fallback)
    {
        return obj[key] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : fallback;
    }

    private static int ParseInt(string value, int fallback)
    {
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static bool GetBool(ServerInstance instance, string key)
    {
        return GetBool(instance, key, false);
    }

    private static bool GetBool(ServerInstance instance, string key, bool fallback)
    {
        return instance.Settings.TryGetValue(key, out var value) && bool.TryParse(value?.ToString(), out var parsed)
            ? parsed
            : fallback;
    }

    private static double GetDouble(IReadOnlyDictionary<string, object?> settings, string key, double fallback)
    {
        return settings.TryGetValue(key, out var value) &&
               double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static async Task CheckTcpPortAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        using var client = new TcpClient();

        await client.ConnectAsync(host, port, linkedCancellation.Token);
    }

    private static string GetConnectableHost(string host)
    {
        return string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "::", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : host;
    }

    private static string GetQueryHost(string host)
    {
        if (!string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(host, "::", StringComparison.OrdinalIgnoreCase))
        {
            return host;
        }

        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up)
                .Select(network => network.GetIPProperties())
                .Where(properties => properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork))
                .SelectMany(properties => properties.UnicastAddresses)
                .Select(address => address.Address)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                ?.ToString()
                ?? Dns.GetHostEntry(Dns.GetHostName())
                .AddressList
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                ?.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

}
