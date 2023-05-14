using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace nopawnstacking;

[StaticConstructorOnStartup, UsedImplicitly]
public static class Main {
    static Main() {
        new Harmony("mute.nopawnstacking").PatchAll(Assembly.GetExecutingAssembly());
    }
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public static class HarmonyPatches {
    [HarmonyPatch(typeof(Pawn_MindState), "MindStateTick"), UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public static class MindStateTickPatch {
        internal static bool IsRealCombatant(Pawn_MindState mindState) {
            return mindState.duty?.def.alwaysShowWeapon is true ||
                   mindState.mentalStateHandler.CurStateDef?.IsAggro is true ||
                   Current.gameInt.tickManager.ticksGameInt <= mindState.lastCombatantTick + 600;
        }

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
                new CodeMatch(OpCodes.Stloc_1),
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldarg_0),
                new CodeMatch(OpCodes.Ldfld, AccessTools.Field(typeof(Pawn_MindState), "pawn")),
                new CodeMatch(OpCodes.Ldloc_1),
                new CodeMatch(OpCodes.Ldc_I4_1),
                new CodeMatch(OpCodes.Ldc_R4), // -1
                new CodeMatch(OpCodes.Ldc_I4_1),
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
                        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MindStateTickPatch), "IsRealCombatant"))
                    )
                    .InstructionEnumeration();
            }

            Log.Error("[Combat Always Collides] Failed to apply collision patch");
            return editor.InstructionEnumeration();
        }
    }

    [HarmonyPatch(typeof(AttackTargetFinder), "DebugDrawNonCombatantTimer_OnGUI"), UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public static class DrawTimerPatch {
        private static void DrawTimer(Pawn pawn, Vector2 pos) {
            var isCombatant = pawn.mindState.anyCloseHostilesRecently;
            if (isCombatant) {
                GenMapUI.DrawThingLabel(pos + new Vector2(0, 16), "colliding", Color.blue);
            }
            else {
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