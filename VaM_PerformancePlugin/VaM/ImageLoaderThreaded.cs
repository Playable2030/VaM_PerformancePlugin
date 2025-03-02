using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using MVR.FileManagement;
using UnityEngine;
using UnityEngine.Networking;
using VaM_PerformancePlugin.extra;
using Object = UnityEngine.Object;
using ThreadPriority = System.Threading.ThreadPriority;

namespace VaM_PerformancePlugin.VaM;

// Not currently used
[SuppressMessage("ReSharper", "InconsistentNaming")]
public class ImageLoaderThreadedPatch
{
    // all shared state the helper thread can read from
    private static readonly ImageLoaderThreadContext _threadContext = new();
    
    // TODO do we care if the thread is running? is there a scenario where we destroy/stop the thread?
    // we only set this to "true" on the main thread
    // we only set this to "false" in the helper thread
    private static volatile bool _running = false;
    private static Thread _thread;
    
    // private static final
    
    private static readonly MethodInfo RemoveCanceledImagesMethodInfo =
        typeof(ImageLoaderThreaded).GetMethod("RemoveCanceledImages", BindingFlags.NonPublic)!;

    private static readonly MethodInfo useCachedTexMethodInfo =
        typeof(ImageLoaderThreaded).GetMethod("UseCachedTex", BindingFlags.NonPublic)!;

    [HarmonyPatch(typeof(ImageLoaderThreaded), "StartThreads")]
    [HarmonyPrefix]
    public static bool StartThreads()
    {
        if (_running || _thread != null)
        {
            return false;
        }
        
        // reset ctx to defaults
        _threadContext.InterruptThread = false;
        
        _running = true;
        _thread = new Thread(ImageLoaderHelperThread.DoWork)
        {
            // TODO make this configurable?
            Priority = ThreadPriority.Normal,
            // IsBackground = false,
            // CurrentUICulture = null,
            // CurrentCulture = null,
            Name = "ImageLoaderTask"
        };

        return false;
    }
    
    [HarmonyPatch(typeof(ImageLoaderThreaded), "StopThreads")]
    [HarmonyPrefix]
    public static bool StopThreads(ref ImageLoaderThreaded __instance)
    {
        if (!_running || _thread == null)
        {
            return false;
        }

        try
        {
            // to try to gracefully shutdown the thread
            _threadContext.InterruptThread = true;
            _thread.Join(TimeSpan.FromSeconds(10));
        }
        catch (Exception e)
        {
            // TODO we should log this and fix edge cases when this happens...
            _thread.Abort();
        }
        finally
        {
            _thread = null;
            _running = false;
        }
        
        return false;
    }
    
    // Start patches to add to queue

    [HarmonyPatch(typeof(ImageLoaderThreaded), nameof(ImageLoaderThreaded.QueueImage))]
    [HarmonyPrefix]
    public static bool QueueImage(ref ImageLoaderThreaded __instance, ImageLoaderThreaded.QueuedImage queuedImage)
    {
        // avoid locking if we're going to do nothing at all
        if (queuedImage is null || _threadContext.WorkQueue is null)
        {
            return false;
        }
        
        // lock and add the image
        _threadContext.Lock.EnterWriteLock();
        try
        {
            _threadContext.WorkQueue.Enqueue(queuedImage);
        }
        finally
        {
            // make sure we exit regardless of errors
            _threadContext.Lock.EnterWriteLock();
        }
        
        return false;
    }
    
    [HarmonyPatch(typeof(ImageLoaderThreaded), nameof(ImageLoaderThreaded.QueueThumbnail))]
    [HarmonyPrefix]
    public static bool QueueThumbnail(ref ImageLoaderThreaded __instance, ImageLoaderThreaded.QueuedImage queuedImage)
    {
        // avoid locking if we're going to do nothing at all
        if (queuedImage is null || _threadContext.WorkQueue is null)
        {
            return false;
        }

        queuedImage.isThumbnail = true;
        
        // lock and add the image
        _threadContext.Lock.EnterWriteLock();
        try
        {
            _threadContext.WorkQueue.Enqueue(queuedImage);
        }
        finally
        {
            // make sure we exit regardless of errors
            _threadContext.Lock.EnterWriteLock();
        }
        
        return false;
    }
    
    [HarmonyPatch(typeof(ImageLoaderThreaded), nameof(ImageLoaderThreaded.QueueThumbnailImmediate))]
    [HarmonyPrefix]
    public static bool QueueThumbnailImmediate(ref ImageLoaderThreaded __instance, ImageLoaderThreaded.QueuedImage queuedImage)
    {
        // avoid locking if we're going to do nothing at all
        if (queuedImage is null || _threadContext.PriorityWorkQueue is null)
        {
            return false;
        }

        queuedImage.isThumbnail = true;
        
        // lock and add the image
        _threadContext.PriorityLock.EnterWriteLock();
        try
        {
            _threadContext.PriorityWorkQueue.Enqueue(queuedImage);
        }
        finally
        {
            // make sure we exit regardless of errors
            _threadContext.PriorityLock.EnterWriteLock();
        }
        
        return false;
    }
    
    // End patches to add to queue
    
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
    
    [HarmonyPatch]
    [HarmonyFinalizer]
    public static Exception Finalizer(Exception __exception)
    {
        return new PluginException("ImageLoaderThreaded had an exception", __exception);
    }
}

// all data here needs to be readonly, aka immutable
public class ImageLoaderThreadContext
{
    public Queue<ImageLoaderThreaded.QueuedImage> WorkQueue { get; } = new();
    public ReaderWriterLockSlim Lock { get; } = new(LockRecursionPolicy.SupportsRecursion);

    // for images that need to be loaded "immediately". we achieve this by clearing this queue first
    public Queue<ImageLoaderThreaded.QueuedImage> PriorityWorkQueue { get; } = new();
    public ReaderWriterLockSlim PriorityLock { get; } = new(LockRecursionPolicy.SupportsRecursion);
    
    // set when an item is added to the queue, to improve efficiency over having the background thread just poll the queue
    public AutoResetEvent AddedItemSignal { get; } = new(false);

    // AutoResetEvent
    // "interrupt" signal
    public volatile bool InterruptThread = false;
}

public class ImageLoaderHelperThread 
{
    private ImageLoaderHelperThread()
    {
    }

    public static void DoWork(object data)
    {
        DoWork((ImageLoaderThreadContext) data);    
    }
    
    public static void DoWork(ImageLoaderThreadContext ctx)
    {
        while (true)
        {
            // if asked to stop, exit gracefully
            if (ctx.InterruptThread)
            {
                return;
            }

            // check priority queue first
            // will finish the queue before moving on
            ProcessQueues(ctx);
            
            // wait for more images to be added to avoid spinning
            // will wait for 100 ms or until an image is added
            // TODO have this be a while loop that listens for "true"?
            // TODO when will this be cleared?
            ctx.AddedItemSignal.WaitOne(TimeSpan.FromMilliseconds(100));
            
        }
    }

    public static void ProcessQueues(ImageLoaderThreadContext ctx)
    {
        while (ctx.PriorityWorkQueue.Count > 0 || ctx.WorkQueue.Count > 0)
        {
            // if asked to stop, exit gracefully
            if (ctx.InterruptThread)
            {
                return;
            }

            ImageLoaderThreaded.QueuedImage qi;
            // check PriorityQueue first
            if (ctx.PriorityWorkQueue.Count > 0)
            {
                qi = ctx.PriorityWorkQueue.Dequeue();
            }
            else // load the normal Queue item
            {
                qi = ctx.WorkQueue.Dequeue();
            }
            
            // TODO IMPL
        }
    }
}