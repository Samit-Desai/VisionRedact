using System.Collections;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace VisionRedact.Presentation.Wpf.Infrastructure;

// for the WPF "wrong context" errors (an object of one Dispatcher used by another). logs once, on the first one:
// which visuals and app resources belong to another Dispatcher, which thread owns them, which debug-tool
// assemblies are loaded.
// why: under the Visual Studio debugger its XAML tools (WpfTap) can leave a second Dispatcher on the UI thread,
// new objects get that one while the window has the first. not an app bug, and this puts the proof in the log.
// diagnostics only, never changes state. called from App.OnDispatcherUnhandledException
internal static class DispatcherAffinityProbe
{
    // so a big foreign subtree can't flood the log
    private const int MaxReported = 25;

    // walks the window + app resources, writes one report. UI thread
    public static void LogForeignObjects(Window window, System.Windows.Application app, ILogger? logger)
    {
        if (logger is null)
        {
            return;
        }

        // diagnostics must never cause a second crash, hence the catch-all
        try
        {
            // the window's Dispatcher is home, look for visuals and resources on any other
            var home = window.Dispatcher;
            var foreign = new List<string>();
            var visited = 0;
            Walk(window, home, foreign, ref visited, path: window.GetType().Name);
            ScanResources(app.Resources, home, foreign, "Application.Resources");

            // debugger / diagnostics assemblies. WpfTap here points at the VS XAML tools
            var tools = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name ?? string.Empty)
                .Where(n => n.Contains("VisualStudio", StringComparison.OrdinalIgnoreCase) || n.Contains("WpfTap", StringComparison.OrdinalIgnoreCase) || n.Contains("XamlDiagnostics", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToArray();

            // a new object takes Dispatcher.CurrentDispatcher. if that isn't the window's (same thread), every new
            // element (item containers, clip geometries) is "from the wrong context". so log every way of asking
            // "which Dispatcher?", plus what a throw-away DrawingVisual gets
            var current = Dispatcher.CurrentDispatcher;
            var fresh = new DrawingVisual().Dispatcher;
            logger.LogError(
                "Dispatcher identities on thread {Thread}: window {Window}, application {App}, CurrentDispatcher {Current}, FromThread {FromThread}, new visual {Fresh}; window dispatcher shutting down: {Shutdown}.",
                Environment.CurrentManagedThreadId,
                Describe(home),
                Describe(app.Dispatcher),
                Describe(current),
                Describe(Dispatcher.FromThread(Thread.CurrentThread)),
                Describe(fresh),
                home.HasShutdownStarted);

            // the foreign objects found in the walk above
            logger.LogError(
                "Dispatcher affinity probe: UI dispatcher thread {UiThread}; {Visited} visuals scanned; {Count} foreign object(s):{NewLine}{Objects}{NewLine}Debugger attached: {Debugger}. Tool assemblies: {Tools}",
                Describe(home),
                visited,
                foreign.Count,
                Environment.NewLine,
                foreign.Count == 0 ? "  (none found in the visual tree or application resources)" : string.Join(Environment.NewLine, foreign),
                Environment.NewLine,
                System.Diagnostics.Debugger.IsAttached,
                tools.Length == 0 ? "(none)" : string.Join(", ", tools));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Dispatcher affinity probe failed.");
        }
    }

    // depth-first over the visual tree, records foreign subtrees. try/catch b/c touching a foreign object can throw
    private static void Walk(DependencyObject node, Dispatcher home, List<string> foreign, ref int visited, string path)
    {
        visited++;
        if (node.Dispatcher != home)
        {
            // only the root of a foreign subtree, its children most likely came with it
            if (foreign.Count < MaxReported)
            {
                foreign.Add($"  visual  {path}  owned by {Describe(node.Dispatcher)}");
            }

            return;
        }

        int count;
        try
        {
            count = VisualTreeHelper.GetChildrenCount(node);
        }
        catch (Exception)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            DependencyObject child;
            try
            {
                child = VisualTreeHelper.GetChild(node, i);
            }
            catch (Exception)
            {
                continue;
            }

            var name = child is FrameworkElement { Name.Length: > 0 } fe ? $"{child.GetType().Name}#{fe.Name}" : child.GetType().Name;
            Walk(child, home, foreign, ref visited, path + " > " + name);
        }
    }

    // shared resources (brushes, styles...) and the merged dictionaries. one foreign resource used everywhere
    // would explain errors everywhere
    private static void ScanResources(ResourceDictionary dictionary, Dispatcher home, List<string> foreign, string path)
    {
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Value is DispatcherObject { Dispatcher: { } owner } value && owner != home && foreign.Count < MaxReported)
            {
                foreign.Add($"  resource  {path}[{entry.Key}] ({value.GetType().Name})  owned by {Describe(owner)}");
            }
        }

        for (var i = 0; i < dictionary.MergedDictionaries.Count; i++)
        {
            var merged = dictionary.MergedDictionaries[i];
            ScanResources(merged, home, foreign, $"{path} > {merged.Source?.OriginalString ?? $"merged[{i}]"}");
        }
    }

    // thread + hash code, so two Dispatchers on one thread can be told apart. null = frozen, any thread may use it
    private static string Describe(Dispatcher? dispatcher) =>
        dispatcher is null
            ? "no dispatcher (free-threaded)"
            : $"thread {dispatcher.Thread.ManagedThreadId} '{dispatcher.Thread.Name ?? "unnamed"}' (dispatcher #{dispatcher.GetHashCode():X8})";
}
