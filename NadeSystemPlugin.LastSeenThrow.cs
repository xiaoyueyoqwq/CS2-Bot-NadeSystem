using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NadeSystem;

public partial class NadeSystemPlugin : BasePlugin
{
    private const float LastSeenMinAge = 0.2f;
    private const float LastSeenMaxAge = 2.5f;
    private const float LastSeenMinDist = 250f;
    private const float LastSeenMaxDist = 1200f;
    private const float LastSeenMaxYawDeg = 45f;
    private const float LastSeenMollyDist = 600f;
    private const float LastSeenThrowCooldown = 10f;
    private const float LastSeenEyeHeight = 64f;
    private const float LastSeenEyeForward = 12f;
    private const float LastSeenMinSpeed = 500f;
    private const float LastSeenMaxSpeed = 1300f;

    // * Native/coach combat throws from CCSBot last-seen, not lineup JSON
    private void CheckLastSeenThrows()
    {
        if (!UsesNativeBotNades) return;
        if (_roundOver) return;

        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (rules?.GameRules?.FreezePeriod == true) return;

        float now = Server.CurrentTime;
        foreach (var bot in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            try
            {
                TryLastSeenThrow(bot, now);
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[NadeSystem] last-seen throw error: {ex.Message}");
            }
        }
    }

    private void TryLastSeenThrow(CCSPlayerController bot, float now)
    {
        if (!bot.IsValid || !bot.IsBot) return;
        if (bot.HasBeenControlledByPlayerThisRound) return;
        if (!bot.PawnIsAlive) return;

        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return;
        if (pawn.MoveType == MoveType_t.MOVETYPE_LADDER) return;

        var ccsBot = pawn.Bot;
        if (ccsBot == null) return;
        if (ccsBot.IsEnemyVisible) return;
        if (ccsBot.IsLastEnemyDead) return;

        uint botIdx = (uint)bot.Index;
        if (_lastSeenThrowReadyAt.TryGetValue(botIdx, out float readyAt) && now < readyAt)
            return;

        float sawAt = ccsBot.LastSawEnemyTimestamp;
        float age = now - sawAt;
        if (age < LastSeenMinAge || age > LastSeenMaxAge) return;

        if (_lastSeenThrowSawAt.TryGetValue(botIdx, out float processedSaw) && processedSaw == sawAt)
            return;

        var last = ccsBot.LastEnemyPosition;
        var origin = pawn.AbsOrigin;
        if (last == null || origin == null) return;

        float lastX = last.X;
        float lastY = last.Y;
        float lastZ = last.Z;

        float yawRad = pawn.EyeAngles.Y * (MathF.PI / 180f);
        float eyeX = origin.X + MathF.Cos(yawRad) * LastSeenEyeForward;
        float eyeY = origin.Y + MathF.Sin(yawRad) * LastSeenEyeForward;
        float eyeZ = origin.Z + LastSeenEyeHeight;

        float dx = lastX - eyeX;
        float dy = lastY - eyeY;
        float dz = lastZ - eyeZ;
        float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (dist < LastSeenMinDist || dist > LastSeenMaxDist) return;

        float yawToLast = MathF.Atan2(dy, dx) * (180f / MathF.PI);
        if (MathF.Abs(DeltaYawDegrees(yawToLast, pawn.EyeAngles.Y)) > LastSeenMaxYawDeg)
            return;

        if (!LastSeenHasWorldLoS(eyeX, eyeY, eyeZ, lastX, lastY, lastZ))
            return;

        string? gtype = PickLastSeenGrenade(pawn, dist);
        if (gtype == null) return;

        // Consume this last-seen episode even if the probability roll fails,
        // so coach_hard 70% does not become ~99% across the 2.5s window.
        _lastSeenThrowSawAt[botIdx] = sawAt;

        float chance = _botNadesMode switch
        {
            "coach_hard" => 0.70f,
            "coach_normal" => 0.50f,
            "coach_easy" => 0.30f,
            _ => 0.25f,
        };
        if (Random.Shared.NextDouble() >= chance) return;

        float speed = Math.Clamp(dist * 1.05f, LastSeenMinSpeed, LastSeenMaxSpeed);
        float inv = 1f / dist;
        float loft = Math.Clamp(dist * 0.20f, 80f, 280f);
        var spawnOrigin = new Vector(eyeX, eyeY, eyeZ);
        var velocity = new Vector(dx * inv * speed, dy * inv * speed, dz * inv * speed + loft);
        float yaw = MathF.Atan2(velocity.Y, velocity.X) * (180f / MathF.PI);
        float hDist = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
        float pitch = -MathF.Atan2(velocity.Z, hDist) * (180f / MathF.PI);
        var angles = new QAngle(pitch, yaw, 0f);
        int teamNum = bot.TeamNum;

        Server.NextFrame(() =>
        {
            try
            {
                if (!UsesNativeBotNades) return;
                var botPawn = bot.PlayerPawn?.Value;
                if (botPawn == null || !botPawn.IsValid || !bot.PawnIsAlive) return;

                if (!SpawnLastSeenProjectile(bot, botPawn, gtype, teamNum, spawnOrigin, velocity, angles))
                    return;

                ConsumeGrenadeWeapon(botPawn, gtype);
                _lastSeenThrowReadyAt[botIdx] = Server.CurrentTime + LastSeenThrowCooldown;
                AnnounceGrenadeThrow(bot, gtype);
                Server.PrintToConsole(
                    $"[NadeSystem] last-seen [{gtype}] bot=[{bot.PlayerName}] " +
                    $"dist={dist:F0} origin=({eyeX:F0},{eyeY:F0},{eyeZ:F0}) " +
                    $"target=({lastX:F0},{lastY:F0},{lastZ:F0})");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[NadeSystem] last-seen spawn error: {ex.Message}");
            }
        });
    }

    private static float DeltaYawDegrees(float a, float b)
    {
        float d = a - b;
        while (d > 180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }

    private static bool LastSeenHasWorldLoS(
        float eyeX, float eyeY, float eyeZ,
        float lastX, float lastY, float lastZ)
    {
        try
        {
            var start = new Vector(eyeX, eyeY, eyeZ);
            var end = new Vector(lastX, lastY, lastZ);
            var opts = new TraceOptions { InteractsWith = Masks.SolidBrushOnly };
            var res = Trace.TraceEndShape(start, end, options: opts);
            return res.Fraction >= 0.99f;
        }
        catch
        {
            return false;
        }
    }

    private static string? PickLastSeenGrenade(CCSPlayerPawn pawn, float dist)
    {
        bool hasMolly = HasGrenadeWeapon(pawn, "molotov");
        bool hasHe = HasGrenadeWeapon(pawn, "he");
        bool hasFlash = HasGrenadeWeapon(pawn, "flash");
        if (dist < LastSeenMollyDist)
        {
            if (hasMolly) return "molotov";
            if (hasHe) return "he";
            if (hasFlash) return "flash";
        }
        else
        {
            if (hasHe) return "he";
            if (hasMolly) return "molotov";
            if (hasFlash) return "flash";
        }
        return null;
    }

    private static bool HasGrenadeWeapon(CCSPlayerPawn pawn, string gtype)
        => FindGrenadeWeapon(pawn, gtype) != null;

    private static CBasePlayerWeapon? FindGrenadeWeapon(CCSPlayerPawn pawn, string gtype)
    {
        var weapons = pawn.WeaponServices?.MyWeapons;
        if (weapons == null) return null;
        foreach (var handle in weapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid) continue;
            string name = weapon.DesignerName;
            if (string.IsNullOrEmpty(name)) continue;
            bool match = gtype switch
            {
                "he" => name == "weapon_hegrenade",
                "molotov" => name is "weapon_molotov" or "weapon_incgrenade",
                "flash" => name == "weapon_flashbang",
                _ => false,
            };
            if (match) return weapon;
        }
        return null;
    }

    private static void ConsumeGrenadeWeapon(CCSPlayerPawn pawn, string gtype)
    {
        var weapon = FindGrenadeWeapon(pawn, gtype);
        if (weapon == null || !weapon.IsValid) return;
        weapon.AcceptInput("Kill");
    }

    private bool SpawnLastSeenProjectile(
        CCSPlayerController bot,
        CCSPlayerPawn botPawn,
        string gtype,
        int teamNum,
        Vector origin,
        Vector velocity,
        QAngle angles)
    {
        if (gtype == "flash")
        {
            var flash = Utilities.CreateEntityByName<CFlashbangProjectile>("flashbang_projectile");
            if (flash == null)
            {
                Server.PrintToConsole("[NadeSystem] last-seen flash CreateEntityByName null");
                return false;
            }
            flash.TeamNum = (byte)teamNum;
            flash.Thrower.Raw = botPawn.EntityHandle.Raw;
            flash.OriginalThrower.Raw = botPawn.EntityHandle.Raw;
            flash.OwnerEntity.Raw = botPawn.EntityHandle.Raw;
            flash.InitialPosition.X = origin.X;
            flash.InitialPosition.Y = origin.Y;
            flash.InitialPosition.Z = origin.Z;
            flash.InitialVelocity.X = velocity.X;
            flash.InitialVelocity.Y = velocity.Y;
            flash.InitialVelocity.Z = velocity.Z;
            flash.Elasticity = 0.33f;
            flash.Teleport(origin, angles, velocity);
            flash.DispatchSpawn();
            flash.Teleport(origin, angles, velocity);
            return true;
        }

        if (gtype == "he")
        {
            var he = _heCreate.Invoke(
                origin.Handle,
                origin.Handle,
                velocity.Handle,
                velocity.Handle,
                botPawn.Handle,
                44);
            if (he == null || !he.IsValid)
            {
                Server.PrintToConsole("[NadeSystem] last-seen HE native Create returned null");
                return false;
            }
            he.TeamNum = (byte)teamNum;
            he.Thrower.Raw = botPawn.EntityHandle.Raw;
            he.OriginalThrower.Raw = botPawn.EntityHandle.Raw;
            he.OwnerEntity.Raw = botPawn.EntityHandle.Raw;
            return true;
        }

        if (gtype == "molotov")
        {
            int molotovItemDef = teamNum == (int)CsTeam.CounterTerrorist ? 48 : 46;
            var molotov = _molotovCreate.Invoke(
                origin.Handle,
                origin.Handle,
                velocity.Handle,
                velocity.Handle,
                botPawn.Handle,
                molotovItemDef);
            if (molotov == null || !molotov.IsValid)
            {
                Server.PrintToConsole("[NadeSystem] last-seen molotov native Create returned null");
                return false;
            }
            molotov.TeamNum = (byte)teamNum;
            molotov.Thrower.Raw = botPawn.EntityHandle.Raw;
            molotov.OriginalThrower.Raw = botPawn.EntityHandle.Raw;
            molotov.OwnerEntity.Raw = botPawn.EntityHandle.Raw;
            return true;
        }

        return false;
    }
}
