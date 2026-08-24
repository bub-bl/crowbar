using Crowbar.UI;

namespace Crowbar.UI.Tests;

/// <summary>Contract referenced by the <c>@implements</c> directive test.</summary>
public interface IRazorTestContract
{
}

/// <summary>
/// Test-only base class used by the <c>@inherits</c>/<c>@implements</c>/lifecycle
/// tests. It lives in the test assembly, so those tests compile their templates
/// with an explicit reference to this assembly.
/// </summary>
public abstract class RazorTestBase : RazorTemplateBase
{
    public int InitializedCount { get; protected set; }
    public int ParametersCount { get; protected set; }
    public int AfterRenderCount { get; protected set; }
    public bool AllowRender { get; set; } = true;
    public int BuildVersion { get; set; }

    protected override bool ShouldRender() => AllowRender;
    protected override int BuildHash() => BuildVersion;
    protected override void OnInitialized() => InitializedCount++;
    protected override void OnParametersSet() => ParametersCount++;
}
