using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace VaM_PerformancePlugin.VaM;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class MirrorCopyPhysicsComponentsPatch
{
    private const string replaceString = "r";
    private const string withString = "l";

    [HarmonyPatch(typeof(MirrorCopyPhysicsComponents), nameof(MirrorCopyPhysicsComponents.copyRigidbody))]
    [HarmonyPrefix]
    public static bool copyRigidbody(ref MirrorCopyPhysicsComponents __instance)
    {
        var instanceTraverse = Traverse.Create(__instance);

        // unpack fields for use later
        var RBMap = instanceTraverse.Field<Dictionary<string, Rigidbody>>("RBMap").Value;

        var transformMap = instanceTraverse.Field<Dictionary<string, Transform>>("transformMap").Value;
        // end unpack

        if (!(bool)(Object)__instance.copyFromRoot) return false;

        foreach (var copyFromRbJointName in __instance.getCopyFromRBJointNames())
        {
            var copyFromRb = __instance.getCopyFromRB(copyFromRbJointName);
            
            var str1 = copyFromRb.transform.parent.name;
            if (str1.Length > 0 && str1.StartsWith(replaceString))
            {
                str1 = withString + str1.Substring(1);
            }
            
            if (!RBMap.TryGetValue(str1, out var rigidbody))
            {
                var transform1 = __instance.getTransform(str1);
                if (!transform1)
                {
                    
                    var trans = copyFromRb.transform.parent.name;
                    if (trans.Length > 0 && trans.StartsWith(replaceString))
                    {
                        trans = withString + trans.Substring(1);
                    }

                    var transform2 = __instance.getTransform(trans);
                    if (transform2)
                    {
                        var gameObject = new GameObject(str1)
                        {
                            name = str1
                        };
                        if (!transformMap.ContainsKey(str1))
                            transformMap.Add(str1, gameObject.transform);
                        gameObject.transform.parent = transform2;
                        gameObject.transform.localPosition =
                            MirrorVector(__instance.invertAxis, copyFromRb.transform.localPosition);
                        gameObject.transform.localRotation = copyFromRb.transform.localRotation;
                        gameObject.transform.localScale = copyFromRb.transform.localScale;
                        rigidbody = gameObject.AddComponent<Rigidbody>();
                        RBMap.Add(str1, rigidbody);
                    }
                    else
                    {
                        Debug.LogError("could not find parent transform " + trans + " during copy");
                    }
                }
                else
                {
                    rigidbody = transform1.gameObject.AddComponent<Rigidbody>();
                    RBMap.Add(str1, rigidbody);
                }
            }

            if (!rigidbody || !copyFromRb)
            {
                continue;
            }
            
            rigidbody.gameObject.layer = copyFromRb.gameObject.layer;
            rigidbody.mass = copyFromRb.mass;
            rigidbody.drag = copyFromRb.drag;
            rigidbody.angularDrag = copyFromRb.angularDrag;
            rigidbody.useGravity = copyFromRb.useGravity;
            rigidbody.interpolation = copyFromRb.interpolation;
            rigidbody.collisionDetectionMode = copyFromRb.collisionDetectionMode;
            rigidbody.isKinematic = copyFromRb.isKinematic;
            rigidbody.constraints = copyFromRb.constraints;
            copyCapsuleCollider(__instance.invertAxis,copyFromRb.gameObject, rigidbody.gameObject);
            copyBoxCollider(__instance.invertAxis,copyFromRb.gameObject, rigidbody.gameObject);
            foreach (Transform transform3 in copyFromRb.transform)
                if (!transform3.GetComponent<Rigidbody>())
                {
                    var component1 = transform3.GetComponent<CapsuleCollider>();
                    var component2 = transform3.GetComponent<BoxCollider>();
                    if (!component1 && !component2)
                    {
                        continue;
                    }
                    
                    var name = transform3.name;
                    GameObject go = null;
                    var str2 = name;
                    if (name.Length > 0 && name.StartsWith(replaceString))
                    {
                        str2 = withString + name.Substring(1);
                    }

                    foreach (Transform transform4 in rigidbody.transform)
                    {
                        if (transform4.name == str2)
                        {
                            go = transform4.gameObject;
                        }
                    }

                    if (!go)
                    {
                        go = new GameObject(str2)
                        {
                            name = str2
                        };
                        if (!transformMap.ContainsKey(str2)) transformMap.Add(str2, go.transform);

                        go.transform.parent = rigidbody.transform;
                        go.transform.localPosition =
                            MirrorVector(__instance.invertAxis, transform3.localPosition);
                        go.transform.localRotation =
                            MirrorQuaternion(__instance.invertAxis, transform3.localRotation);
                        go.transform.localScale = transform3.localScale;
                    }

                    copyCapsuleCollider(__instance.invertAxis, transform3.gameObject, go);
                    copyBoxCollider(__instance.invertAxis,transform3.gameObject, go);
                    go.layer = transform3.gameObject.layer;
                }
        }

        return false;
    }

    // inlined helper methods that are small
    private static Vector3 MirrorVector(MirrorCopyPhysicsComponents.InvertAxis invertAxis, Vector3 inVector)
    {
        var vector3 = inVector;
        switch (invertAxis)
        {
            case MirrorCopyPhysicsComponents.InvertAxis.X:
                vector3.x = -vector3.x;
                break;
            case MirrorCopyPhysicsComponents.InvertAxis.Y:
                vector3.y = -vector3.y;
                break;
            case MirrorCopyPhysicsComponents.InvertAxis.Z:
                vector3.z = -vector3.z;
                break;
        }

        return vector3;
    }

    private static Quaternion MirrorQuaternion(MirrorCopyPhysicsComponents.InvertAxis invertAxis, Quaternion inQuat)
    {
        var quaternion = inQuat;
        switch (invertAxis)
        {
            case MirrorCopyPhysicsComponents.InvertAxis.X:
                quaternion.y = -quaternion.y;
                quaternion.z = -quaternion.z;
                break;
            case MirrorCopyPhysicsComponents.InvertAxis.Y:
                quaternion.x = -quaternion.x;
                quaternion.z = -quaternion.z;
                break;
            case MirrorCopyPhysicsComponents.InvertAxis.Z:
                quaternion.x = -quaternion.x;
                quaternion.y = -quaternion.y;
                break;
        }

        return quaternion;
    }
    
    
    static void copyCapsuleCollider(MirrorCopyPhysicsComponents.InvertAxis invertAxis, GameObject refgo, GameObject go)
    {
        CapsuleCollider component = refgo.GetComponent<CapsuleCollider>();
        CapsuleCollider capsuleCollider = go.GetComponent<CapsuleCollider>();
        
        if (!(component))
        {
            return;
        }
        if (!capsuleCollider)
        {
            capsuleCollider = go.AddComponent<CapsuleCollider>();
        }

        capsuleCollider.isTrigger = component.isTrigger;
        capsuleCollider.sharedMaterial = component.sharedMaterial;
        Vector3 vector3 = MirrorVector(invertAxis, component.center);
        capsuleCollider.center = vector3;
        capsuleCollider.radius = component.radius;
        capsuleCollider.height = component.height;
        capsuleCollider.direction = component.direction;
    }

    static void copyBoxCollider(MirrorCopyPhysicsComponents.InvertAxis invertAxis, GameObject refgo, GameObject go)
    {
        BoxCollider component = refgo.GetComponent<BoxCollider>();
        BoxCollider boxCollider = go.GetComponent<BoxCollider>();
        
        if (!component)
        {
            return;
        }
        if (!boxCollider)
        {
            boxCollider = go.AddComponent<BoxCollider>();
        }

        boxCollider.isTrigger = component.isTrigger;
        boxCollider.sharedMaterial = component.sharedMaterial;
        Vector3 vector3 = MirrorVector(invertAxis, component.center);
        boxCollider.center = vector3;
        boxCollider.size = component.size;
    }

}