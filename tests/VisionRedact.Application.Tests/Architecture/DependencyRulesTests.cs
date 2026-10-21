using System.Reflection;
using System.Xml.Linq;
using VisionRedact.Application.Playback;
using VisionRedact.Domain.Detections;

namespace VisionRedact.Application.Tests.Architecture;

// the dependency rule (ADR-0001): Domain uses only .NET, Application adds only the logging abstractions.
// checked twice: the compiled references, and the csproj (an unused reference never shows up in the first one)
public sealed class DependencyRulesTests
{
    private static readonly string[] Forbidden =
    [
        "Emgu", "Microsoft.ML.OnnxRuntime", "PresentationCore", "PresentationFramework", "WindowsBase", "System.Xaml",
        "Prism", "DryIoc", "Serilog", "VisionRedact.Infrastructure", "VisionRedact.Presentation",
    ];

    [Fact]
    public void Domain_references_only_dotnet_itself()
    {
        var references = typeof(Detection).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.All(references, name => Assert.True(
            name.StartsWith("System", StringComparison.Ordinal) || name is "netstandard" or "mscorlib",
            $"VisionRedact.Domain references {name}"));
    }

    [Fact]
    public void Application_references_no_library_and_no_outer_layer()
    {
        var references = typeof(PlaybackSession).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.Contains("VisionRedact.Domain", references);
        Assert.DoesNotContain(references, name => Forbidden.Any(f => name.StartsWith(f, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("VisionRedact.Domain", new string[0], new string[0])]
    [InlineData("VisionRedact.Application", new[] { "VisionRedact.Domain" }, new[] { "Microsoft.Extensions.Logging.Abstractions" })]
    public void Project_files_declare_only_the_allowed_references(string project, string[] projects, string[] packages)
    {
        var file = Path.Combine(RepositoryRoot(), "src", project, project + ".csproj");
        var xml = XDocument.Load(file);

        var declaredProjects = xml.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension((string)e.Attribute("Include")!))
            .ToArray();
        var declaredPackages = xml.Descendants("PackageReference").Select(e => (string)e.Attribute("Include")!).ToArray();

        Assert.Equal(projects.Order(), declaredProjects.Order());
        Assert.Equal(packages.Order(), declaredPackages.Order());
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VisionRedact.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("VisionRedact.slnx not found above the test output.");
    }
}
