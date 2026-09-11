using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NadeSystem;

public sealed class NadeSystemConfig : BasePluginConfig
{
    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 1;

    [JsonPropertyName("Mode")]
    public string Mode { get; set; } = "normal";
}

public partial class NadeSystemPlugin : BasePlugin, IPluginConfig<NadeSystemConfig>
{
    public NadeSystemConfig Config { get; set; } = new();

    private static readonly HashSet<string> SupportedModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "off", "less", "normal", "more", "max",
        "native", "coach_easy", "coach_normal", "coach_hard",
    };

    private bool UsesReplayDatabase => _botNadesMode is "less" or "normal" or "more" or "max";
    private bool UsesLegacyReplayFeatures => UsesReplayDatabase;
    private bool UsesNativeBotNades => _botNadesMode is "native" or "coach_easy" or "coach_normal" or "coach_hard";

    public void OnConfigParsed(NadeSystemConfig config)
    {
        Config = config ?? new NadeSystemConfig();
        var mode = (Config.Mode ?? "normal").Trim().ToLowerInvariant();
        if (!SupportedModes.Contains(mode))
        {
            Server.PrintToConsole($"[NadeSystem] Unknown configured mode '{Config.Mode}', using normal.");
            mode = "normal";
        }

        _botNadesMode = mode;
    }

    // Native and coach modes leave grenade decisions and physics to Valve's CCSBot.
    private void ApplyModeSettings()
    {
        if (!UsesNativeBotNades)
        {
            Server.ExecuteCommand("bot_allow_grenades 0");
            Server.ExecuteCommand("sv_bot_buy_grenade_chance 0");
            return;
        }

        Server.ExecuteCommand("bot_allow_grenades 1");
        int chance = _botNadesMode switch
        {
            "coach_easy" => 20,
            "coach_normal" => 45,
            "coach_hard" => 80,
            _ => 35,
        };
        Server.ExecuteCommand($"sv_bot_buy_grenade_chance {chance}");

        int smoke = _botNadesMode switch
        {
            "coach_easy" => 4,
            "coach_normal" => 5,
            "coach_hard" => 6,
            _ => 5,
        };
        int flash = _botNadesMode switch
        {
            "coach_easy" => 2,
            "coach_normal" => 3,
            "coach_hard" => 4,
            _ => 3,
        };
        int he = _botNadesMode switch
        {
            "coach_easy" => 1,
            "coach_normal" => 3,
            "coach_hard" => 4,
            _ => 2,
        };
        int molotov = _botNadesMode switch
        {
            "coach_easy" => 2,
            "coach_normal" => 4,
            "coach_hard" => 6,
            _ => 3,
        };

        Server.ExecuteCommand($"sv_bot_buy_smoke_weight {smoke}");
        Server.ExecuteCommand($"sv_bot_buy_flash_weight {flash}");
        Server.ExecuteCommand($"sv_bot_buy_hegrenade_weight {he}");
        Server.ExecuteCommand($"sv_bot_buy_molotov_weight {molotov}");
        int decoy = _botNadesMode == "coach_hard" ? 1 : 0;
        Server.ExecuteCommand($"sv_bot_buy_decoy_weight {decoy}");
    }
}
