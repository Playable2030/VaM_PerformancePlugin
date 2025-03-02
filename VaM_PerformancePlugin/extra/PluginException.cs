using System;

namespace VaM_PerformancePlugin.extra;

// Dummy exception class so we can add additional info to exceptions that may come from patched methods
[Serializable]
public class PluginException: Exception
{
    public PluginException(string message) : base(message) { }
    public PluginException(string message, Exception innerException) : base(message, innerException) { }
}