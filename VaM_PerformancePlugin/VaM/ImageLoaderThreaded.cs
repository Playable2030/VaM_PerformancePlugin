using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MVR.FileManagement;
using UnityEngine;
using UnityEngine.Networking;

namespace VaM_PerformancePlugin.VaM;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public class ImageLoaderThreadedPatch
{
    private static readonly MethodInfo RemoveCanceledImagesMethodInfo =
        typeof(ImageLoaderThreaded).GetMethod("RemoveCanceledImages", BindingFlags.NonPublic)!;

    private static readonly MethodInfo useCachedTexMethodInfo =
        typeof(ImageLoaderThreaded).GetMethod("UseCachedTex", BindingFlags.NonPublic)!;

    // TODO future improvements, make this multi-threaded? it's not i/o bound currently, and should be...
    // TODO review conditionals and see if there's perf gains to be had via re-ordering logic
    [HarmonyPatch(typeof(ImageLoaderThreaded), "PreprocessImageQueue")]
    [HarmonyPrefix]
    public static bool PreprocessImageQueue(ref ImageLoaderThreaded __instance,
        ref LinkedList<ImageLoaderThreaded.QueuedImage> ___queuedImages,
        ref Dictionary<string, Texture2D> ___thumbnailCache,
        ref Dictionary<string, Texture2D> ___textureCache,
        ref Dictionary<Texture2D, bool> ___textureTrackedCache,
        ref int ___numRealQueuedImages,
        ref int ___progress,
        ref int ___progressMax,
        // TODO UnityEngine.UI.Text does not resolve, why? do we care?
        ref object ___progressText
    )
    {
        RemoveCanceledImagesMethodInfo.Invoke(__instance, null);
        if (___queuedImages is not { Count: > 0 })
        {
            return false;
        }

        var queuedImage = ___queuedImages.First.Value;
        if (queuedImage == null)
        {
            return false;
        }

        if (!queuedImage.skipCache && queuedImage.imgPath != null && queuedImage.imgPath != "NULL")
        {
            if (queuedImage.isThumbnail)
            {
                if (___thumbnailCache != null && ___thumbnailCache.TryGetValue(queuedImage.imgPath, out var tex))
                {
                    if (!tex)
                    {
                        Debug.LogError("Trying to use cached texture at " + queuedImage.imgPath +
                                       " after it has been destroyed");
                        ___thumbnailCache.Remove(queuedImage.imgPath);
                    }
                    else
                    {
                        useCachedTexMethodInfo.Invoke(___queuedImages, [queuedImage, tex]);
                    }
                }
            }
            else
            {
                if (___textureCache != null &&
                    ___textureCache.TryGetValue(queuedImage.cacheSignature, out var texture2D))
                {
                    if (!texture2D)
                    {
                        Debug.LogError("Trying to use cached texture at " + queuedImage.imgPath +
                                       " after it has been destroyed");
                        ___textureCache.Remove(queuedImage.cacheSignature);
                        ___textureTrackedCache.Remove(texture2D);
                    }
                    else
                    {
                        useCachedTexMethodInfo.Invoke(___queuedImages, [queuedImage, texture2D]);
                    }
                }
            }
        }

        // This is the only change right now, remove a simple usage of Regex
        if (!queuedImage.processed && queuedImage.imgPath != null && queuedImage.imgPath.StartsWith("http"))
        // if (!queuedImage.processed && queuedImage.imgPath != null && Regex.IsMatch(queuedImage.imgPath, "^http"))
        {
            if (CacheManager.CachingEnabled && queuedImage.WebCachePathExists())
            {
                queuedImage.useWebCache = true;
            }
            else
            {
                if (queuedImage.webRequest == null)
                {
                    queuedImage.webRequest = UnityWebRequest.Get(queuedImage.imgPath);
                    queuedImage.webRequest.SendWebRequest();
                }

                if (queuedImage.webRequest.isDone)
                {
                    if (!queuedImage.webRequest.isNetworkError)
                    {
                        if (queuedImage.webRequest.responseCode == 200L)
                        {
                            queuedImage.webRequestData = queuedImage.webRequest.downloadHandler.data;
                            queuedImage.webRequestDone = true;
                        }
                        else
                        {
                            queuedImage.webRequestHadError = true;
                            queuedImage.webRequestDone = true;
                            queuedImage.hadError = true;
                            queuedImage.errorText = new StringBuilder().Append("Error ")
                                .Append(queuedImage.webRequest.responseCode)
                                .ToString();
                        }
                    }
                    else
                    {
                        queuedImage.webRequestHadError = true;
                        queuedImage.webRequestDone = true;
                        queuedImage.hadError = true;
                        queuedImage.errorText = queuedImage.webRequest.error;
                    }
                }
            }
        }

        if (queuedImage.isThumbnail || !(Object)___progressText)
        {
            return false;
        }

        ___progressText = new StringBuilder().Append("[")
            .Append(___progress)
            .Append("/")
            .Append(___progressMax)
            .Append("] ")
            .Append(queuedImage.imgPath)
            .ToString();
        return false;
    }
}