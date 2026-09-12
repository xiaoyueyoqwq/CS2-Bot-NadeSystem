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
    // ═══════════════════════════════════════════════════════════
    //  Special Nades
    // ═══════════════════════════════════════════════════════════
    // Defuse smoke/flash
    // * Spawns and charges an immediate situational grenade
    private void TrySpawnInstantGrenade(CCSPlayerController bot, Vector spawnPos, string gtype, Vector? velocity = null)
    {
        if (!UsesLegacyReplayFeatures) return;
        // In case the bot has been taken over
        bool isTakenOver = bot.HasBeenControlledByPlayerThisRound;
        if (isTakenOver) return;
        bool hasLiveEnemy = Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Any(p => p.IsValid && p.PawnIsAlive
                && ((int)p.TeamNum == 2 || (int)p.TeamNum == 3)
                && (int)p.TeamNum != bot.TeamNum);
        if (!hasLiveEnemy) return;

        var money = bot.InGameMoneyServices;
        if (money == null) return;

        bool isCT     = bot.TeamNum == (int)CsTeam.CounterTerrorist;
        var costTable = isCT ? CostCT : CostT;
        if (!costTable.TryGetValue(gtype, out int cost)) return;
        if (money.Account < cost) return;

        uint botIdx  = (uint)bot.Index;
        if (!HasLockedNadeMoney(botIdx, cost)) return;
        bool isPoor   = _poorBots.Contains((uint)bot.Index);
        int  spendCap = GetRoundSpendCap(isCT, isPoor);
        if (!_roundSpendPerBot.TryGetValue(botIdx, out int alreadySpent))
            alreadySpent = 0;
        bool deduct = alreadySpent < spendCap;

        if (deduct)
        {
            money.Account -= cost;
            Utilities.SetStateChanged(bot, "CCSPlayerController", "m_pInGameMoneyServices");
            _roundSpendPerBot[botIdx] = alreadySpent + cost;
        }
        SpendLockedNadeMoney(botIdx, cost);

        var vel = velocity ?? new Vector(0f, 0f, 0f);
        Server.NextFrame(() =>
        {
            try
            {
                if (!UsesLegacyReplayFeatures) return;
                var botPawn = bot.PlayerPawn?.Value;
                if (botPawn == null || !botPawn.IsValid) return;

                int teamNum = bot.TeamNum;

                if (gtype == "smoke")
                {
                    var smoke = _smokeCreate.Invoke(
                        spawnPos.Handle, spawnPos.Handle,
                        vel.Handle, vel.Handle,
                        botPawn.Handle, 45, teamNum);
                    if (smoke == null || !smoke.IsValid) return;
                    smoke.TeamNum             = (byte)teamNum;
                    smoke.Thrower.Raw         = botPawn.EntityHandle.Raw;
                    smoke.OriginalThrower.Raw = botPawn.EntityHandle.Raw;
                    smoke.OwnerEntity.Raw     = botPawn.EntityHandle.Raw;
                    AnnounceGrenadeThrow(bot, gtype);
                }
                else if (gtype == "flash")
                {
                    var flash = Utilities.CreateEntityByName<CFlashbangProjectile>(
                        "flashbang_projectile");
                    if (flash == null) return;
                    flash.TeamNum             = (byte)teamNum;
                    flash.Thrower.Raw         = botPawn.EntityHandle.Raw;
                    flash.OriginalThrower.Raw = botPawn.EntityHandle.Raw;
                    flash.OwnerEntity.Raw     = botPawn.EntityHandle.Raw;
                    flash.InitialPosition.X   = spawnPos.X;
                    flash.InitialPosition.Y   = spawnPos.Y;
                    flash.InitialPosition.Z   = spawnPos.Z;
                    flash.InitialVelocity.X   = vel.X;
                    flash.InitialVelocity.Y   = vel.Y;
                    flash.InitialVelocity.Z   = vel.Z;
                    flash.Elasticity          = 0.33f;
                    var ang = new QAngle(-90f, 0f, 0f);
                    flash.Teleport(spawnPos, ang, vel);
                    flash.DispatchSpawn();
                    flash.Teleport(spawnPos, ang, vel);
                    AnnounceGrenadeThrow(bot, gtype);
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[NadeSystem] TrySpawnInstantGrenade error: {ex.Message}");
            }
        });
    }

    private const float DefuseCoverSmokeMaxRange = 700f;
    private const float DefuseCoverSmokeHandHeight = 48f;
    private const float DefuseCoverSmokeForward = 16f;
    private const float DefuseCoverSmokeSpeed = 350f;
    private const float DefuseCoverSmokeCloseDist = 80f;

    // * Triggers defensive smoke or flash support during a defuse
    private HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        var defuser = @event.Userid;
        if (defuser == null || !defuser.IsValid) return HookResult.Continue;

        if (!_defuseSmokeUsed)
        {
            bool threwCover = TryThrowDefuseCoverSmoke(defuser);
            if (!threwCover
                && UsesLegacyReplayFeatures
                && defuser.IsBot
                && !defuser.HasBeenControlledByPlayerThisRound)
            {
                var pawn = defuser.PlayerPawn?.Value;
                if (pawn != null && pawn.IsValid && defuser.PawnIsAlive)
                {
                    var pos = pawn.AbsOrigin;
                    if (pos != null)
                    {
                        bool hasDefuser = false;
                        if (pawn.ItemServices != null
                            && pawn.ItemServices.Handle != nint.Zero)
                        {
                            hasDefuser = new CCSPlayer_ItemServices(pawn.ItemServices.Handle).HasDefuser;
                        }

                        if (hasDefuser || Random.Shared.NextDouble() < 0.33)
                        {
                            _defuseSmokeUsed = true;
                            TrySpawnInstantGrenade(defuser, new Vector(pos.X, pos.Y, pos.Z + 5f), "smoke");
                        }
                    }
                }
            }
        }

        if (!UsesLegacyReplayFeatures) return HookResult.Continue;
        if (!defuser.IsBot || defuser.HasBeenControlledByPlayerThisRound)
            return HookResult.Continue;

        var flashPawn = defuser.PlayerPawn?.Value;
        if (flashPawn == null || !flashPawn.IsValid || !defuser.PawnIsAlive)
            return HookResult.Continue;

        var flashPos = flashPawn.AbsOrigin;
        if (flashPos == null) return HookResult.Continue;

        // Defuse flash
        if (!_defuseFlashUsed)
        {
            if (Random.Shared.NextDouble() < 0.20)
            {
                _defuseFlashUsed = true;
                // Don't flash yourself
                _botFlashImmunityUntil[(uint)defuser.Index] = Server.CurrentTime + 2f;
                var flashVel = new Vector(0f, 0f, -800f);
                TrySpawnInstantGrenade(defuser, new Vector(flashPos.X, flashPos.Y, flashPos.Z + 5f), "flash", flashVel);
            }
        }

        return HookResult.Continue;
    }

    // * Inventory underhand smoke from a teammate bot onto planted C4
    private bool TryThrowDefuseCoverSmoke(CCSPlayerController defuser)
    {
        if (_botNadesMode == "off") return false;
        if (CountAliveEnemyBots(defuser.TeamNum) < 1) return false;

        var bomb = Utilities
            .FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
            .FirstOrDefault();
        if (bomb == null || !bomb.IsValid) return false;
        var bombPos = bomb.AbsOrigin;
        if (bombPos == null) return false;

        float c4X = bombPos.X, c4Y = bombPos.Y, c4Z = bombPos.Z;
        var thrower = FindDefuseCoverSmokeBot(defuser, c4X, c4Y, c4Z);
        if (thrower == null) return false;

        _defuseSmokeUsed = true;
        int teamNum = thrower.TeamNum;

        Server.NextFrame(() =>
        {
            try
            {
                if (_botNadesMode == "off" || !thrower.IsValid || !thrower.IsBot || !thrower.PawnIsAlive
                    || thrower.HasBeenControlledByPlayerThisRound)
                {
                    _defuseSmokeUsed = false;
                    return;
                }

                var pawn = GetActiveLivePawn(thrower);
                if (pawn == null || !pawn.IsValid || !HasGrenadeWeapon(pawn, "smoke"))
                {
                    _defuseSmokeUsed = false;
                    return;
                }

                var origin = pawn.AbsOrigin;
                if (origin == null)
                {
                    _defuseSmokeUsed = false;
                    return;
                }

                ComputeDefuseCoverUnderhand(
                    origin.X, origin.Y, origin.Z, c4X, c4Y, c4Z,
                    out float spawnX, out float spawnY, out float spawnZ,
                    out float velX, out float velY, out float velZ);

                var spawnOrigin = new Vector(spawnX, spawnY, spawnZ);
                var velocity = new Vector(velX, velY, velZ);
                var smoke = _smokeCreate.Invoke(
                    spawnOrigin.Handle, spawnOrigin.Handle,
                    velocity.Handle, velocity.Handle,
                    pawn.Handle, 45, teamNum);
                if (smoke == null || !smoke.IsValid)
                {
                    _defuseSmokeUsed = false;
                    return;
                }

                smoke.TeamNum = (byte)teamNum;
                smoke.Thrower.Raw = pawn.EntityHandle.Raw;
                smoke.OriginalThrower.Raw = pawn.EntityHandle.Raw;
                smoke.OwnerEntity.Raw = pawn.EntityHandle.Raw;
                ConsumeGrenadeWeapon(pawn, "smoke");
                AnnounceGrenadeThrow(thrower, "smoke");
                Server.PrintToConsole(
                    $"[NadeSystem] defuse-cover [smoke] bot=[{thrower.PlayerName}] " +
                    $"origin=({spawnX:F0},{spawnY:F0},{spawnZ:F0}) " +
                    $"c4=({c4X:F0},{c4Y:F0},{c4Z:F0})");
            }
            catch (Exception ex)
            {
                _defuseSmokeUsed = false;
                Server.PrintToConsole($"[NadeSystem] defuse-cover smoke error: {ex.Message}");
            }
        });

        return true;
    }

    private static int CountAliveEnemyBots(int friendlyTeam)
    {
        int count = 0;
        foreach (var p in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!p.IsValid || !p.IsBot || !p.PawnIsAlive) continue;
            if ((int)p.TeamNum != 2 && (int)p.TeamNum != 3) continue;
            if ((int)p.TeamNum == friendlyTeam) continue;
            if (p.HasBeenControlledByPlayerThisRound) continue;
            count++;
        }
        return count;
    }

    private CCSPlayerController? FindDefuseCoverSmokeBot(
        CCSPlayerController defuser, float c4X, float c4Y, float c4Z)
    {
        CCSPlayerController? best = null;
        float bestDist = float.MaxValue;
        uint defuserIdx = (uint)defuser.Index;

        foreach (var p in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!p.IsValid || !p.IsBot || !p.PawnIsAlive) continue;
            if ((uint)p.Index == defuserIdx) continue;
            if (p.TeamNum != defuser.TeamNum) continue;
            if (p.HasBeenControlledByPlayerThisRound) continue;

            var pawn = GetActiveLivePawn(p);
            if (pawn == null || !pawn.IsValid) continue;
            if (pawn.MoveType == MoveType_t.MOVETYPE_LADDER) continue;
            if (!HasGrenadeWeapon(pawn, "smoke")) continue;

            var origin = pawn.AbsOrigin;
            if (origin == null) continue;
            float d = Dist3D(origin.X, origin.Y, origin.Z, c4X, c4Y, c4Z);
            if (d > DefuseCoverSmokeMaxRange) continue;
            if (d >= bestDist) continue;

            bestDist = d;
            best = p;
        }

        return best;
    }

    private static void ComputeDefuseCoverUnderhand(
        float ox, float oy, float oz,
        float c4X, float c4Y, float c4Z,
        out float spawnX, out float spawnY, out float spawnZ,
        out float velX, out float velY, out float velZ)
    {
        float dx = c4X - ox;
        float dy = c4Y - oy;
        float handZ = oz + DefuseCoverSmokeHandHeight;
        float dz = c4Z - handZ;
        float hDist = MathF.Sqrt(dx * dx + dy * dy);
        spawnZ = handZ;
        if (hDist > 1f)
        {
            float invH = 1f / hDist;
            spawnX = ox + dx * invH * DefuseCoverSmokeForward;
            spawnY = oy + dy * invH * DefuseCoverSmokeForward;
        }
        else
        {
            spawnX = ox;
            spawnY = oy;
        }

        float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (dist < DefuseCoverSmokeCloseDist)
        {
            if (hDist > 1f)
            {
                float invH = 1f / hDist;
                velX = dx * invH * 80f;
                velY = dy * invH * 80f;
            }
            else
            {
                velX = 0f;
                velY = 0f;
            }
            velZ = 40f;
            return;
        }

        float inv = 1f / dist;
        float loft = Math.Clamp(dist * 0.15f, 40f, 120f);
        velX = dx * inv * DefuseCoverSmokeSpeed;
        velY = dy * inv * DefuseCoverSmokeSpeed;
        velZ = dz * inv * DefuseCoverSmokeSpeed + loft;
    }

    // Plant smoke
    // * Triggers smoke support when a bot starts planting
    private HookResult OnBombBeginPlant(EventBombBeginplant @event, GameEventInfo info)
    {
        if (!UsesLegacyReplayFeatures) return HookResult.Continue;
        if (_plantSmokeUsed) return HookResult.Continue;

        var bot = @event.Userid;
        if (bot == null || !bot.IsValid || !bot.IsBot) return HookResult.Continue;
        if (bot.HasBeenControlledByPlayerThisRound) return HookResult.Continue;

        if (Random.Shared.NextDouble() >= 0.33) return HookResult.Continue;

        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid || !bot.PawnIsAlive)
            return HookResult.Continue;

        var pos = pawn.AbsOrigin;
        if (pos == null) return HookResult.Continue;

        _plantSmokeUsed = true;
        TrySpawnInstantGrenade(bot, new Vector(pos.X, pos.Y, pos.Z + 5f), "smoke");
        return HookResult.Continue;
    }

    // * Dispatches damage-driven grenade reactions
    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        HandleMolotovEscape(@event);
        HandleRetaliationHE(@event);

        return HookResult.Continue;
    }
    // Put out the fire
    // * Uses smoke to help a bot escape sustained fire damage
    private void HandleMolotovEscape(EventPlayerHurt @event)
    {
        if (!UsesLegacyReplayFeatures) return;
        var victim = @event.Userid;
        if (victim == null || !victim.IsValid || !victim.IsBot) return;
        if (victim.HasBeenControlledByPlayerThisRound) return;

        var pawn = victim.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid || !victim.PawnIsAlive) return;

        string weapon = @event.Weapon ?? "";
        bool isMolotovDmg = weapon.Contains("inferno", StringComparison.OrdinalIgnoreCase)
                         || weapon.Contains("molotov", StringComparison.OrdinalIgnoreCase)
                         || weapon.Contains("incgrenade", StringComparison.OrdinalIgnoreCase);
        if (!isMolotovDmg)
        {
            _botMolotovDmgStart.Remove((uint)victim.Index);
            return;
        }

        int teamNum = victim.TeamNum;
        if (_molotovEscapeSmokeCooldown.TryGetValue(teamNum, out float expiry)
            && Server.CurrentTime < expiry) return;

        uint idx = (uint)victim.Index;
        float now = Server.CurrentTime;

        if (!_botMolotovDmgStart.TryGetValue(idx, out float start))
        {
            _botMolotovDmgStart[idx] = now;
            return;
        }

        if (now - start < 0.3f) return;

        _botMolotovDmgStart.Remove(idx);
        _molotovEscapeSmokeCooldown[teamNum] = now + 20f;

        var pos = pawn.AbsOrigin;
        if (pos == null) return;
        TrySpawnInstantGrenade(victim, new Vector(pos.X, pos.Y, pos.Z + 5f), "smoke");
    }
    // Revenge grenade
    // * Selects and throws a retaliatory explosive grenade
    private void HandleRetaliationHE(EventPlayerHurt @event)
    {
        if (!UsesLegacyReplayFeatures) return;
        var victim = @event.Userid;
        if (victim == null || !victim.IsValid || !victim.IsBot) return;
        if (victim.HasBeenControlledByPlayerThisRound) return;

        var victimPawn = victim.PlayerPawn?.Value;
        if (victimPawn == null || !victimPawn.IsValid || !victim.PawnIsAlive) return;

        var attacker = @event.Attacker;
        if (attacker == null || !attacker.IsValid || attacker.IsBot || !attacker.PawnIsAlive) return;

        if (attacker.TeamNum == victim.TeamNum) return;   // only retaliate against the enemies
        if (_roundOver) return;

        // No retaliation HE/molotov within 1s of victim firing
        if (FiredRecently(victim, 1f)) return;

        string weapon = @event.Weapon ?? "";
        bool isHE      = weapon.Contains("hegrenade",  StringComparison.OrdinalIgnoreCase);
        bool isMolotov = weapon.Contains("molotov",    StringComparison.OrdinalIgnoreCase)
                    || weapon.Contains("incgrenade",  StringComparison.OrdinalIgnoreCase)
                    || weapon.Contains("inferno",     StringComparison.OrdinalIgnoreCase);
        if (!isHE && !isMolotov) return;

        var atkPos = GetActiveLivePawn(attacker)?.AbsOrigin;
        if (atkPos == null) return;

        string map = Server.MapName;
        int teamNum = victim.TeamNum;
        // less / normal / more mode: retaliation cooldown per team (7s)
        if (_botNadesMode == "normal" || _botNadesMode == "more" || _botNadesMode == "less")
        {
            if (_retaliationCooldown.TryGetValue(teamNum, out float cdExpiry)
                && Server.CurrentTime < cdExpiry) return;
        }
        // less / normal / more mode: limit total he+molotov spawned per hurt event
        int retaliationLimit = int.MaxValue;
        if (_botNadesMode == "normal" || _botNadesMode == "more" || _botNadesMode == "less")
        {
            var vPos = victimPawn.AbsOrigin;
            int aliveTeamSize = Utilities
                .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
                .Count(p =>
                {
                    if (!p.IsValid || (int)p.TeamNum != victim.TeamNum) return false;
                    if (vPos == null) return false;
                    var pp = GetActiveLivePawn(p)?.AbsOrigin;
                    if (pp == null) return false;
                    return Dist3D(vPos.X, vPos.Y, vPos.Z, pp.X, pp.Y, pp.Z) <= 800f;
                });
            retaliationLimit = aliveTeamSize < 1 ? 1 : aliveTeamSize;
        }
        int retaliationSpawned = 0;

        // Build candidate list (single pass: filter then sort)
        // primary  : satisfies both direction and distance check  -> first
        // secondary: nearest projectilePosition to victim         -> ascending
        var vPosForSort = victimPawn.AbsOrigin;
        var candidates = _mapNades
            .Where(g =>
            {
                string gt = g.GrenadeType; // lowercase since LoadDb
                if (gt != "he" && gt != "molotov" && gt != "incgrenade") return false;
                float d = Dist3D(atkPos.X, atkPos.Y, atkPos.Z,
                                 g.LandingPosition.X, g.LandingPosition.Y, g.LandingPosition.Z);
                if (d > 200f) return false;
                if (IsOnCooldown(g.Id)) return false;
                return true;
            })
            .OrderByDescending(g => FacesThrowDirection(victimPawn, g) ? 1 : 0)
            .ThenBy(g => vPosForSort == null ? 0f :
                Dist3D(vPosForSort.X, vPosForSort.Y, vPosForSort.Z,
                       g.ProjectilePosition.X, g.ProjectilePosition.Y, g.ProjectilePosition.Z))
            .ToList();

        // Loop-invariant purchase context; GetRoundSpendCap walks the entity
        // table for gamerules, so resolve it once instead of per candidate.
        var money = victim.InGameMoneyServices;
        if (money == null) return;
        bool isCT     = victim.TeamNum == (int)CsTeam.CounterTerrorist;
        var costTable = isCT ? CostCT : CostT;
        uint botIdx   = (uint)victim.Index;
        bool isPoor   = _poorBots.Contains(botIdx);
        int  spendCap = GetRoundSpendCap(isCT, isPoor);

        foreach (var g in candidates)
        {
            if (retaliationSpawned >= retaliationLimit) break;

            string gt = g.GrenadeType; // lowercase since LoadDb

            if (!costTable.TryGetValue(gt, out int cost)) continue;
            if (money.Account < cost) continue;
            if (!HasLockedNadeMoney(botIdx, cost)) continue;
            // Less mode: enforce per-bot round limits (counts retaliation nades).
            if (_botNadesMode == "less" && !LessModeAllows(gt, botIdx)) continue;

            if (!_roundSpendPerBot.TryGetValue(botIdx, out int alreadySpent)) alreadySpent = 0;
            bool deduct = alreadySpent < spendCap;
            if (deduct)
            {
                money.Account -= cost;
                Utilities.SetStateChanged(victim, "CCSPlayerController", "m_pInGameMoneyServices");
                _roundSpendPerBot[botIdx] = alreadySpent + cost;
            }
            SpendLockedNadeMoney(botIdx, cost);

            RegisterCooldown(g.Id, gt);
            SpawnProjectile(victim, g);
            // Normal mode: counts retaliation nades toward per-team round limit.
            if (_botNadesMode == "normal")
                IncrementCount(gt, victim.TeamNum);
            // Less mode: counts retaliation nades toward per-bot round limit.
            else if (_botNadesMode == "less")
                IncrementBotCount(gt, botIdx);
            retaliationSpawned++;
        }
        // Write cooldown after retaliation completes (less / normal / more)
        if ((_botNadesMode == "normal" || _botNadesMode == "more" || _botNadesMode == "less") && retaliationSpawned > 0)
            _retaliationCooldown[teamNum] = Server.CurrentTime + 7f;
    }
}
