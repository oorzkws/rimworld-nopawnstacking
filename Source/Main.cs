using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using RimWorld;
using System;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using Verse;
using Verse.AI;
using CodeMatch = HarmonyLib.CodeMatch;

namespace nopawnstacking;

[StaticConstructorOnStartup, UsedImplicitly]
public static class Main {
    static Main() {
        new Harmony("mute.nopawnstacking").PatchAll(Assembly.GetExecutingAssembly());
    }
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public static class HarmonyPatches {
    internal static bool IsRealCombatant(Pawn_MindState mindState) {
        if (mindState.duty?.def.alwaysShowWeapon is true) {
            return true;
        }
        if (mindState.enemyTarget is not null) {
            return true;
        }
        var lastTick = new []{mindState.lastEngageTargetTick, mindState.lastAttackTargetTick, mindState.lastMeleeThreatHarmTick, mindState.lastMeleeThreatHarmTick}.Max();
        if (Current.gameInt.tickManager.ticksGameInt <= lastTick + 600) {
            return true;
        }
        if (mindState.mentalStateHandler is var mentalState) {
            if (mentalState.CurStateDef?.IsAggro is true) {
                return true;
            }
            if (mentalState.CurStateDef == MentalStateDefOf.PanicFlee && mentalState.CurState.age <= 3600) {
                return true;
            }
        }
        if (mindState.pawn.RaceProps.intelligence == Intelligence.Animal && Current.gameInt.tickManager.ticksGameInt <= mindState.lastCombatantTick + 600) {
            return true;
        }
        if (mindState.pawn.Drafted && mindState.pawn.IsFighting()) {
            // This is a terrible API to call, performance-wise
            if (mindState.pawn.Map?.attackTargetReservationManager.FirstReservationFor(mindState.pawn) is not null) {
                return true;
            }
        }
        return false;
    }

    internal static bool CheckAndSetCombatantStatus(Pawn_MindState mindState) {
        var retValue = IsRealCombatant(mindState);
        if (mindState.pawn.CurJob is not null) {
            mindState.pawn.CurJob.collideWithPawns = retValue;
        }
        return retValue;
    }

    [HarmonyPatch(typeof(Pawn_MindState), "Reset"), UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public static class MindStateResetPatch {
        internal static void Prefix(ref Pawn_MindState __instance, out int __state) {
            __state = __instance.lastEngageTargetTick;
        }

        internal static void Postfix(ref Pawn_MindState __instance, int __state) {
            __instance.lastEngageTargetTick = __state;
            // This allows fleeing pawns to remain combatants
            CheckAndSetCombatantStatus(__instance);
        }
    }
    
    [HarmonyPatch(typeof(Pawn_MindState), "MindStateTickInterval"), UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public static class MindStateTickPatch {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var editor = new CodeMatcher(instructions, generator);
            // --------------------------ORIGINAL--------------------------
            // int num = (this.anyCloseHostilesRecently ? 24 : 18);
            // this.anyCloseHostilesRecently = PawnUtility.EnemiesAreNearby(this.pawn, num, true, -1f, 1);
            var matchPattern = new[] {
                new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(Pawn_MindState), "anyCloseHostilesRecently")),
                new CodeMatch(OpCodes.Brtrue_S), // Don't believe [HarmonyDebug]'s lies, it's _S
                new CodeMatch(OpCodes.Ldc_I4_S), // 18
                new CodeMatch(OpCodes.Br_S),
                new CodeMatch(OpCodes.Ldc_I4_S), // 24
                new CodeMatch(OpCodes.Stloc_2),
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(Pawn_MindState), "pawn")),
                new CodeMatch(OpCodes.Ldloc_2),
                new CodeMatch(OpCodes.Ldc_I4_1),
                new CodeMatch(OpCodes.Ldc_R4), // -1
                new CodeMatch(OpCodes.Ldc_I4_1),
                new CodeMatch(OpCodes.Ldc_I4_0),
                new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(PawnUtility), "EnemiesAreNearby"))
            };
            editor.Start().MatchStartForward(matchPattern);

            if (editor.IsValid) {
                // --------------------------MODIFIED--------------------------
                // this.anyCloseHostilesRecently = MindStateTickPatch.IsRealCombatant(this);
                return editor
                    .RemoveInstructions(matchPattern.Length)
                    .InsertAndAdvance(
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HarmonyPatches), "IsRealCombatant"))
                    )
                    .InstructionEnumeration();
            }

            Log.Error("[Combat Always Collides] Failed to apply collision patch");
            return editor.InstructionEnumeration();
        }
    }

    [HarmonyPatch(typeof(JobGiver_AIFightEnemy), "UpdateEnemyTarget")]
    public static class AIFightEnemyUpdateTargetPatch {
        public static void Postfix(ref Pawn pawn) {
            if (pawn.CurJob is not {} curJob || pawn.mindState is not { } mindState)
                return;
            curJob.collideWithPawns = mindState.anyCloseHostilesRecently && (mindState.enemyTarget is not null || Current.gameInt.tickManager.ticksGameInt <= mindState.lastEngageTargetTick + 600);
        }
    }
    
    // Disabled: more expensive pathing for little gain
    //[HarmonyPatch(typeof(Pawn_PathFollower), "PatherTick"), UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public static class PatherTickPatch {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var editor = new CodeMatcher(instructions, generator);
            // --------------------------ORIGINAL--------------------------
            // if (this.WillCollideWithPawnAt(this.pawn.Position, true))
            var matchPattern = new[] {
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(Pawn_PathFollower), "pawn")),
                new CodeMatch(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Thing), "Position")),
                new CodeMatch(OpCodes.Ldc_I4_1),
                new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(Pawn_PathFollower), "WillCollideWithPawnAt"))
            };
            editor.Start().MatchStartForward(matchPattern);

            if (editor.IsValid) {
                // --------------------------MODIFIED--------------------------
                // if (this.WillCollideWithPawnAt(this.pawn.Position, false))
                return editor
                    .Advance(4) // -> (true)
                    .Set(OpCodes.Ldc_I4_0, null)
                    .InstructionEnumeration();
            }

            Log.Error("[Combat Always Collides] Failed to apply pathertick patch");
            return editor.InstructionEnumeration();
        }
    }

    [HarmonyPatch(typeof(PawnUtility), "AnyPawnBlockingPathAt")]
    public static class AnyPawnBlockingPathAtPatch {
        internal static bool ShouldActAsIfHadCollideWithPawnsJob(Pawn pawn, bool defaultReturn) {
            if (defaultReturn)
                return true;
            if (pawn.mindState is not { } mindState)
                return false;
            return mindState.anyCloseHostilesRecently;
        }
        internal static bool ShouldCollideOnlyWithStandingPawns(Pawn pawn, bool defaultReturn) {
            if (!defaultReturn || pawn.mindState is not { } mindState)
                return defaultReturn;
            return !mindState.anyCloseHostilesRecently;
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var editor = new CodeMatcher(instructions, generator);
            // --------------------------ORIGINAL--------------------------
            // PawnBlockingPathAt(c, forPawn, actAsIfHadCollideWithPawnsJob, collideOnlyWithStandingPawns, forPathFinder, useId)
            var matchPattern = new[] { // Just grabbing the two args we want to change
                new CodeMatch(OpCodes.Ldarg_2),
                new CodeMatch(OpCodes.Ldarg_3),
            };
            editor.Start().MatchStartForward(matchPattern);

            if (editor.IsValid) {
                // --------------------------MODIFIED--------------------------
                // PawnBlockingPathAt(c, forPawn, actAsIfHadCollideWithPawnsJob || forPawn.mindState?.anyCloseHostilesRecently, collideOnlyWithStandingPawns && !forPawn.mindState?.anyCloseHostilesRecently, forPathFinder, useId)
                return editor
                    .RemoveInstructions(matchPattern.Length)
                    .InsertAndAdvance(
                        new CodeInstruction(OpCodes.Ldarg_1),
                        new CodeInstruction(OpCodes.Ldarg_2),
                        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AnyPawnBlockingPathAtPatch), "ShouldActAsIfHadCollideWithPawnsJob")),
                        new CodeInstruction(OpCodes.Ldarg_1),
                        new CodeInstruction(OpCodes.Ldarg_3),
                        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AnyPawnBlockingPathAtPatch), "ShouldCollideOnlyWithStandingPawns"))
                    )
                    .InstructionEnumeration();
            }

            Log.Error("[Combat Always Collides] Failed to apply AnyPawnBlockingPathAt patch");
            return editor.InstructionEnumeration();
        }
    }
    
    [HarmonyPatch(typeof(Pawn_PathFollower), "WillCollideWithPawnAt")]
    public static class WillCollideWithPawnAtPatch {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var editor = new CodeMatcher(instructions, generator);
            // --------------------------ORIGINAL--------------------------
            // actAsIfIHadCollideWithPawnsJob: false, forceOnlyStanding || (pawn.IsShambler && !pawn.mindState.anyCloseHostilesRecently)
            var matchPattern = new[] { // Just grabbing the two args we want to change
                new CodeMatch(OpCodes.Ldc_I4_0),
                new CodeMatch(OpCodes.Ldarg_2),
                new CodeMatch(OpCodes.Brtrue_S),
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldfld), // PathFollower::pawn
                new CodeMatch(OpCodes.Callvirt), // get_IsShambler()
            };
            editor.Start().MatchStartForward(matchPattern);

            if (editor.IsValid) {
                // --------------------------MODIFIED--------------------------
                // actAsIfIHadCollideWithPawnsJob: pawn.mindState.anyCloseHostilesRecently, (forceOnlyStanding && !pawn.mindState.anyCloseHostilesRecently)
                return editor
                    .Advance(0)
                    .SetAndAdvance(OpCodes.Ldarg_0, null)
                    .InsertAndAdvance(
                        new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Pawn_PathFollower), "pawn")),
                        new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Pawn), "mindState")),
                        new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Pawn_MindState), "anyCloseHostilesRecently"))
                    )
                    .Advance(1) // Step past the ldarg_2
                    .RemoveInstructions(4) // remove brtrue through callvirt
                    .MatchStartForward(new CodeMatch(OpCodes.Ldc_I4_1)) // Move to the now-errant ldc_i4_1 and remove it, it's an orphaned jump
                    .ThrowIfFalse("Second return statement not found, please report this", cm => cm.IsValid)
                    .RemoveInstruction()
                    .InstructionEnumeration();
            }

            Log.Error("[Combat Always Collides] Failed to apply WillCollideWithPawnAt patch");
            return editor.InstructionEnumeration();
        }
    }

    [HarmonyPatch(typeof(AttackTargetFinder), "DebugDrawNonCombatantTimer_OnGUI"), UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public static class DrawTimerPatch {
        private static void DrawTimer(Pawn pawn, Vector2 pos) {
            var isCombatant = pawn.mindState.anyCloseHostilesRecently && pawn.kindDef.collidesWithPawns;
            if (isCombatant) {
                var jobColliding = pawn.CurJob is not null && (pawn.CurJob.collideWithPawns || pawn.CurJob.def.collideWithPawns || pawn.jobs.curDriver.collideWithPawns);
                GenMapUI.DrawThingLabel(pos + new Vector2(0, 16), "colliding", jobColliding ? Color.blue : Color.cyan);
            } else {
                GenMapUI.DrawThingLabel(pos + new Vector2(0, 16), "non-colliding", Color.grey);
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator) {
            var editor = new CodeMatcher(instructions, generator);
            // --------------------------ORIGINAL--------------------------
            // if (pawn.IsCombatant()){...
            editor.Start().MatchStartForward(
                new CodeMatch(OpCodes.Ldloc_3),
                new CodeMatch(OpCodes.Call, AccessTools.Method(typeof(PawnUtility), "IsCombatant"))
            );

            if (editor.IsValid) {
                // --------------------------MODIFIED--------------------------
                // DrawTimerPatch.DrawTimer(pawn, vector)
                // if (pawn.IsCombatant()){...
                return editor
                    .InsertAndAdvance(
                        new CodeInstruction(OpCodes.Ldloc_3), // Pawn
                        new CodeInstruction(OpCodes.Ldloc_S, 5),
                        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DrawTimerPatch), "DrawTimer"))
                    )
                    .InstructionEnumeration();
            }

            Log.Error("[Combat Always Collides] Failed to apply debug drawing patch, this will not affect mod functionality");
            return editor.InstructionEnumeration();
        }
    }
}