﻿using BattleTech;
using BattleTech.UI;
using Harmony;
using Localize;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static LootMagnet.LootMagnet;

namespace LootMagnet {

    [HarmonyPatch]
    public static class Contract_GenerateSalvage {

        // Private method can't be patched by annotations, so use MethodInfo
        public static MethodInfo TargetMethod() {
            return AccessTools.Method(typeof(Contract), "GenerateSalvage");
        }

        public static void Prefix(Contract __instance) {
            Mod.Log.Info($"== Resolving salvage for contract:'{__instance.Name}' / '{__instance.GUID}' with result:{__instance.TheMissionResult}");
        }
    }

    [HarmonyPatch(typeof(Contract), "CompleteContract")]
    public static class Contract_CompleteContract {
        
        public static void Prefix(Contract __instance, MissionResult result, bool isGoodFaithEffort) {
            if (__instance != null && !__instance.ContractTypeValue.IsSkirmish) {
                SimGameState simulation = HBS.LazySingletonBehavior<UnityGameInstance>.Instance.Game.Simulation;

                ModState.Employer = __instance.GetTeamFaction("ecc8d4f2-74b4-465d-adf6-84445e5dfc230");
                SimGameReputation employerRep = simulation.GetReputation(ModState.Employer);
                ModState.EmployerRep = employerRep;
                ModState.EmployerRepRaw = simulation.GetRawReputation(ModState.Employer);
                ModState.IsEmployerAlly = simulation.IsCareerFactionAlly(ModState.Employer);
                ModState.MRBRating = simulation.GetCurrentMRBLevel() - 1; // Normalize to 0 indexing
                Mod.Log.Info($"At contract start, Player has MRB:{ModState.MRBRating}  Employer:({ModState.Employer}) EmployerRep:{ModState.EmployerRep} / EmployerAllied:{ModState.IsEmployerAlly}");
            }            
        }
    }

    [HarmonyPatch(typeof(ListElementController_SalvageMechPart_NotListView), "RefreshInfoOnWidget")]
    [HarmonyPatch(new Type[] { typeof(InventoryItemElement_NotListView) })]
    public static class ListElementController_SalvageMechPart_RefreshInfoOnWidget {
        public static void Postfix(ListElementController_SalvageMechPart_NotListView __instance, InventoryItemElement_NotListView theWidget, MechDef ___mechDef, SalvageDef ___salvageDef) {
            Mod.Log.Debug($"LEC_SMP_NLV:RIOW - entered");
            if (___salvageDef.RewardID != null && ___salvageDef.RewardID.Contains("_qty")) {
                int qtyIdx = ___salvageDef.RewardID.IndexOf("_qty");
                string countS = ___salvageDef.RewardID.Substring(qtyIdx + 4);
                int count = int.Parse(countS);
                Mod.Log.Debug($"LEC_SMP_NLV:RIOW - found quantity {count}, changing mechdef");

                DescriptionDef currentDesc = ___mechDef.Chassis.Description;
                string newUIName = $"{currentDesc.UIName} <lowercase>[QTY:{count}]</lowercase>";

                Text newPartName = new Text(newUIName, new object[] { });
                theWidget.mechPartName.SetText(newPartName);
            }
        }
    }

    [HarmonyPatch(typeof(Contract), "AddToFinalSalvage")]
    [HarmonyAfter("io.github.denadan.CustomComponents")]
    public static class Contract_AddToFinalSalvage {
        
        public static void Prefix(Contract __instance, ref SalvageDef def) {
            Mod.Log.Debug($"C:ATFS - entered.");
            if (def?.RewardID != null) {
                if (def.RewardID.Contains("_qty")) {
                    Mod.Log.Debug($"  Salvage ({def.Description.Name}) has rewardID:({def.RewardID}) with multiple quantities");
                    int qtyIdx = def.RewardID.IndexOf("_qty");
                    string countS = def.RewardID.Substring(qtyIdx + 4);
                    Mod.Log.Debug($"  Salvage ({def.Description.Name}) with rewardID:({def.RewardID}) will be given count: {countS}");
                    int count = int.Parse(countS);
                    def.Count = count;
                } else {
                    Mod.Log.Debug($"  Salvage ({def.Description.Name}) has rewardID:({def.RewardID})");
                    List<string> compPartIds = ModState.CompensationParts.Select(sd => sd.RewardID).ToList();
                    if (compPartIds.Contains(def.RewardID)) {
                        Mod.Log.Debug($" Found item in compensation that was randomly assigned.");
                    }
                }
            } else {
                Mod.Log.Debug($"  RewardId was null for def:({def?.Description?.Name})");
            }
        }
    }

    [HarmonyPatch(typeof(Contract), "FinalizeSalvage")]
    public static class Contract_FinalizeSalvage { 

        public static void Postfix(Contract __instance) {
            Mod.Log.Debug("C:FS entered.");
        }
    }


    [HarmonyPatch(typeof(AAR_SalvageScreen), "CalculateAndAddAvailableSalvage")]
    public static class AAR_SalvageScreen_CalculateAndAddAvailableSalvage {

        public static bool Prefix(AAR_SalvageScreen __instance, Contract ___contract, ref int ___totalSalvageMadeAvailable) {
            Mod.Log.Debug("AAR_SS:CAAAS entered.");

            // Calculate potential salvage, which will be rolled up at this point (including mechs!)
            ModState.PotentialSalvage = ___contract.GetPotentialSalvage();

            // Sort by price, since other functions depend on it
            ModState.PotentialSalvage.Sort(new Helper.SalvageDefByCostDescendingComparer());

            // Check for holdback
            bool hasMechParts = ModState.PotentialSalvage.FirstOrDefault(sd => sd.Type != SalvageDef.SalvageType.COMPONENT) != null;
            bool canHoldback = ModState.Employer.DoesGainReputation;
            float triggerChance = Helper.GetHoldbackTriggerChance();
            float holdbackRoll = LootMagnet.Random.Next(101);
            Mod.Log.Info($"Holdback roll:{holdbackRoll}% triggerChance:{triggerChance}% hasMechParts:{hasMechParts} canHoldback:{canHoldback}");

            if (canHoldback && hasMechParts && holdbackRoll <= triggerChance) {
                Mod.Log.Info($"Holdback triggered, determining disputed mech parts.");
                Helper.CalculateHoldback(ModState.PotentialSalvage);
                Helper.CalculateCompensation(ModState.PotentialSalvage);
            }

            ___totalSalvageMadeAvailable = ModState.PotentialSalvage.Count - ModState.HeldbackParts.Count;
            Mod.Log.Debug($"Setting totalSalvageMadeAvailable = potentialSalvage: {ModState.PotentialSalvage.Count} - heldbackParts: {ModState.HeldbackParts.Count}");

            if (ModState.HeldbackParts.Count > 0) {
                UIHelper.ShowHoldbackDialog(___contract, __instance);
            } else {
                // Roll up any remaining salvage and widget-ize it
                List<SalvageDef> rolledUpSalvage = Helper.RollupSalvage(ModState.PotentialSalvage);
                Helper.CalculateAndAddAvailableSalvage(__instance, rolledUpSalvage);
            }

            return false;
        }
    }
}
