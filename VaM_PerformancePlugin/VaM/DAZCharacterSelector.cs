using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using MeshVR;

namespace VaM_PerformancePlugin.VaM;

public class DAZCharacterSelectorMeta(DAZCharacterSelector parentInstance)
{
  public DAZCharacterSelector ParentInstance { get; } = parentInstance;

  private DAZClothingItem[] _maleClothingItems = [];
  private DAZClothingItem[] _femaleClothingItems = [];
  private DAZClothingItem[] _clothingItemById = [];
  private DAZClothingItem[] _clothingItemByBackupId = [];
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class DAZCharacterSelectorPatch
{
  
  // patch constructors/destructors to "add" new fields
  private static Dictionary<DAZCharacterSelector, DAZCharacterSelectorMeta> metaFieldInstances = new();

  [HarmonyPatch(typeof(DAZCharacterSelector), MethodType.Constructor)]
  [HarmonyPostfix]
  private static void CTOR(ref DAZCharacterSelector __instance)
  {
    DAZCharacterSelectorMeta newMetaFields = new DAZCharacterSelectorMeta(__instance);
    metaFieldInstances.Add(__instance, newMetaFields);
  }
  
  [HarmonyPatch(typeof(DAZCharacterSelector), "OnDestroy")]
  [HarmonyPrefix]
  private static bool OnDestroy(ref DAZCharacterSelector __instance)
  {
    // we don't need to keep track of this instance, and it's "new" fields anymore
    metaFieldInstances.Remove(__instance);
    
    // we actually want to run the original method
    return true;
  }
  // end new fields
  

  [HarmonyPatch(typeof(DAZCharacterSelector), nameof(DAZCharacterSelector.InitClothingItems))]
  [HarmonyPrefix]
  [HarmonyWrapSafe]
  public static bool InitClothingItems(ref DAZCharacterSelector __instance)
  {
    DAZCharacterSelectorMeta metaFields = metaFieldInstances[__instance];


    return false;
    
    // this._maleClothingItems = !((UnityEngine.Object) this.maleClothingContainer != (UnityEngine.Object) null) ? new DAZClothingItem[0] : this.maleClothingContainer.GetComponentsInChildren<DAZClothingItem>(true);
    // this._femaleClothingItems = !((UnityEngine.Object) this.femaleClothingContainer != (UnityEngine.Object) null) ? new DAZClothingItem[0] : this.femaleClothingContainer.GetComponentsInChildren<DAZClothingItem>(true);
    // this._clothingItemById = new Dictionary<string, DAZClothingItem>();
    // this._clothingItemByBackupId = new Dictionary<string, DAZClothingItem>();
    // if (Application.isPlaying)
    // {
    //   if (this.clothingItemJSONs == null)
    //     this.clothingItemJSONs = new Dictionary<string, JSONStorableBool>();
    //   if (this.clothingItemToggleJSONs == null)
    //     this.clothingItemToggleJSONs = new List<JSONStorableAction>();
    // }
    // foreach (DAZClothingItem clothingItem in this.clothingItems)
    // {
    //   DAZClothingItem dc = clothingItem;
    //   DAZCharacterSelector characterSelector = this;
    //   if (Application.isPlaying && !this.clothingItemJSONs.ContainsKey(dc.uid))
    //   {
    //     JSONStorableBool jsonStorableBool = new JSONStorableBool("clothing:" + dc.uid, dc.gameObject.activeSelf, new JSONStorableBool.SetJSONBoolCallback(this.SyncClothingItem));
    //     jsonStorableBool.altName = dc.uid;
    //     jsonStorableBool.isRestorable = false;
    //     jsonStorableBool.isStorable = false;
    //     this.RegisterBool(jsonStorableBool);
    //     this.clothingItemJSONs.Add(dc.uid, jsonStorableBool);
    //     JSONStorableAction action = new JSONStorableAction("toggle:" + dc.uid, (JSONStorableAction.ActionCallback) (() => characterSelector.ToggleClothingItem(dc)));
    //     this.RegisterAction(action);
    //     this.clothingItemToggleJSONs.Add(action);
    //   }
    //   dc.characterSelector = this;
    //   if (this._clothingItemById.ContainsKey(dc.uid))
    //     Debug.LogError((object) ("Duplicate uid found for clothing item " + dc.uid));
    //   else
    //     this._clothingItemById.Add(dc.uid, dc);
    //   if (dc.internalUid != null && dc.internalUid != string.Empty && !this._clothingItemById.ContainsKey(dc.internalUid))
    //     this._clothingItemById.Add(dc.internalUid, dc);
    //   if (dc.backupId != null && dc.backupId != string.Empty && !this._clothingItemByBackupId.ContainsKey(dc.backupId))
    //     this._clothingItemByBackupId.Add(dc.backupId, dc);
    //   if (dc.gameObject.activeSelf)
    //     dc.active = true;
    // }
    // if (!Application.isPlaying)
    //   return;
    // List<string> stringList = new List<string>();
    // foreach (JSONStorableBool jsonStorableBool in this.clothingItemJSONs.Values)
    // {
    //   string altName = jsonStorableBool.altName;
    //   if (!this._clothingItemById.ContainsKey(altName))
    //   {
    //     this.DeregisterBool(jsonStorableBool);
    //     stringList.Add(altName);
    //   }
    // }
    // foreach (string key in stringList)
    //   this.clothingItemJSONs.Remove(key);
    // List<JSONStorableAction> jsonStorableActionList = new List<JSONStorableAction>();
    // foreach (JSONStorableAction clothingItemToggleJsoN in this.clothingItemToggleJSONs)
    // {
    //   if (!this._clothingItemById.ContainsKey(clothingItemToggleJsoN.name.Replace("toggle:", string.Empty)))
    //     this.DeregisterAction(clothingItemToggleJsoN);
    //   else
    //     jsonStorableActionList.Add(clothingItemToggleJsoN);
    // }
    // this.clothingItemToggleJSONs = jsonStorableActionList;
  }
}