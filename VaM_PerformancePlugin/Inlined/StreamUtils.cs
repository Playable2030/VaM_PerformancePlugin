using System;
using System.IO;

namespace VaM_PerformancePlugin.Inlined;

// Select methods inlined from SharpZipLib v0.86.0
// The library has security vulnerabilities, and it doesn't seem like we need everything, so pick only the pieces
// we need that we know are "safe"
public static class StreamUtils
{
    
  // TODO can this be replaced? Is there a more efficient way to copy between streams?
    public static void Copy(Stream source, Stream destination, byte[] buffer)
    {
      if (source == null)
        throw new ArgumentNullException(nameof (source));
      if (destination == null)
        throw new ArgumentNullException(nameof (destination));
      if (buffer == null)
        throw new ArgumentNullException(nameof (buffer));
      if (buffer.Length < 128)
        throw new ArgumentException("Buffer is too small", nameof (buffer));
      bool flag = true;
      while (flag)
      {
        int count = source.Read(buffer, 0, buffer.Length);
        if (count > 0)
        {
          destination.Write(buffer, 0, count);
        }
        else
        {
          destination.Flush();
          flag = false;
        }
      }
    }

    // public static void Copy(
    //   Stream source,
    //   Stream destination,
    //   byte[] buffer,
    //   ProgressHandler progressHandler,
    //   TimeSpan updateInterval,
    //   object sender,
    //   string name)
    // {
    //   StreamUtils.Copy(source, destination, buffer, progressHandler, updateInterval, sender, name, -1L);
    // }

    // public static void Copy(
    //   Stream source,
    //   Stream destination,
    //   byte[] buffer,
    //   ProgressHandler progressHandler,
    //   TimeSpan updateInterval,
    //   object sender,
    //   string name,
    //   long fixedTarget)
    // {
    //   if (source == null)
    //     throw new ArgumentNullException(nameof (source));
    //   if (destination == null)
    //     throw new ArgumentNullException(nameof (destination));
    //   if (buffer == null)
    //     throw new ArgumentNullException(nameof (buffer));
    //   if (buffer.Length < 128)
    //     throw new ArgumentException("Buffer is too small", nameof (buffer));
    //   if (progressHandler == null)
    //     throw new ArgumentNullException(nameof (progressHandler));
    //   bool flag1 = true;
    //   DateTime now = DateTime.Now;
    //   long processed = 0;
    //   long target = 0;
    //   if (fixedTarget >= 0L)
    //     target = fixedTarget;
    //   else if (source.CanSeek)
    //     target = source.Length - source.Position;
    //   ProgressEventArgs e1 = new ProgressEventArgs(name, processed, target);
    //   progressHandler(sender, e1);
    //   bool flag2 = true;
    //   while (flag1)
    //   {
    //     int count = source.Read(buffer, 0, buffer.Length);
    //     if (count > 0)
    //     {
    //       processed += (long) count;
    //       flag2 = false;
    //       destination.Write(buffer, 0, count);
    //     }
    //     else
    //     {
    //       destination.Flush();
    //       flag1 = false;
    //     }
    //     if (DateTime.Now - now > updateInterval)
    //     {
    //       flag2 = true;
    //       now = DateTime.Now;
    //       ProgressEventArgs e2 = new ProgressEventArgs(name, processed, target);
    //       progressHandler(sender, e2);
    //       flag1 = e2.ContinueRunning;
    //     }
    //   }
    //   if (flag2)
    //     return;
    //   ProgressEventArgs e3 = new ProgressEventArgs(name, processed, target);
    //   progressHandler(sender, e3);
    // }

}