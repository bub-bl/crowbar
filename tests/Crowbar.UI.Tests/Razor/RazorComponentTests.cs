using Crowbar.UI;

namespace Crowbar.UI.Tests.Razor;

public class RazorComponentTests
{
    [Fact]
    public void ReactiveRenderingEventsAndBindings()
    {
        using var ui = TestUi.Create();
        ui.RegisterRazorComponent("CounterLabel", """
            <span>@Label</span>
            @code {
                [Microsoft.AspNetCore.Components.Parameter] public string Label { get; set; } = string.Empty;
            }
            """, "CounterLabel");
        ui.LoadRazor("""
            <div class="root">
              <button @onclick="@Increment">Count: @count</button>
              <button @onclick="@(() => Increment())">LambdaCount: @count</button>
              <CounterLabel Label="Child component" />
              @if (count > 0) { <label>Visible</label> }
              @foreach (var item in items) { <span>@item</span> }
              <input value="first" @onchange="Changed" @bind-value="@name" />
              <label>@name</label>
              @if (changed) { <label>Changed</label> }
            </div>
            @code {
                private int count;
                private string name = "first";
                private bool changed;
                private readonly string[] items = ["A", "B"];
                private void Increment() { count++; StateHasChanged(); }
                private void Changed(string value) { changed = value == "second"; }
            }
            """, "ReactiveDemo");
        ui.Render();

        var button = TestUi.Find(ui.Screen, p => p is Button);
        Assert.NotNull(button);
        Assert.Contains("A", TestUi.Texts(ui.Screen));

        ui.ProcessPointerDown(button.Layout.X + 1, button.Layout.Y + 1);
        ui.ProcessPointerUp(button.Layout.X + 1, button.Layout.Y + 1);
        ui.Update();
        ui.Render();

        Assert.Contains("Visible", TestUi.Texts(ui.Screen));
        Assert.Contains("Count: 1", TestUi.Texts(ui.Screen));

        var textInput = TestUi.Find(ui.Screen, p => p is TextInput) as TextInput;
        Assert.NotNull(textInput);
        textInput.SetValue("second");
        ui.Update();
        ui.Render();

        Assert.Contains("second", TestUi.Texts(ui.Screen));
        Assert.Contains("Changed", TestUi.Texts(ui.Screen));
        Assert.Contains("Child component", TestUi.Texts(ui.Screen));

        // Editing the bound input keeps focus and value across the rerender.
        ui.ProcessPointerDown(textInput.Layout.Right - 1, textInput.Layout.Y + 1);
        ui.ProcessKey(0x43, true);
        ui.Update();
        ui.Render();
        var rerenderedInput = TestUi.Find(ui.Screen, p => p is TextInput) as TextInput;
        Assert.NotNull(rerenderedInput);
        Assert.Equal("secondc", rerenderedInput.Value);
        Assert.True(rerenderedInput.IsFocused);
    }

    [Fact]
    public void EditorStyleComponentPatternCompiles()
    {
        // Mirrors the components shipped in the Editor (unqualified [Parameter]
        // via @using Microsoft.AspNetCore.Components, explicit RazorPanel base,
        // RenderFragment parameters and @code members).
        using var ui = TestUi.Create();
        ui.RegisterRazorComponent("EditorCard", """
            @inherits Crowbar.UI.RazorPanel
            @namespace Crowbar.Editor.Ui.Components
            @using Microsoft.AspNetCore.Components

            <div class="card-box">
                @ChildContent
            </div>

            @code {
                [Parameter]
                public RenderFragment? ChildContent { get; set; }
            }
            """, "EditorCard");
        ui.LoadRazor("""
            <EditorCard><span>editor content</span></EditorCard>
            """, "EditorRoot");
        ui.Render();
        Assert.Contains("editor content", TestUi.Texts(ui.Screen));
    }

    [Fact]
    public void ChildContentIsSplicedAndPreserved()
    {
        using var ui = TestUi.Create();
        ui.RegisterRazorComponent("Card", """
            <div class="card"><div class="inner">@ChildContent</div></div>
            @code {
                [Microsoft.AspNetCore.Components.Parameter]
                public Microsoft.AspNetCore.Components.RenderFragment? ChildContent { get; set; }
            }
            """, "Card");
        ui.RegisterRazorComponent("NestedLabel", """
            <span>@Label</span>
            @code {
                [Microsoft.AspNetCore.Components.Parameter] public string Label { get; set; } = string.Empty;
            }
            """, "NestedLabel");
        ui.LoadRazor("""
            <div class="root">
              <Card>
                <span class="card-title">Card title</span>
                <input value="@name" @bind-value="name" />
                <NestedLabel Label="inside card" />
                <label>@name</label>
              </Card>
            </div>
            @code {
                private string name = "first";
            }
            """, "ChildContentDemo");
        ui.Render();

        Assert.Contains("Card title", TestUi.Texts(ui.Screen));
        Assert.Contains("first", TestUi.Texts(ui.Screen));
        Assert.Contains("inside card", TestUi.Texts(ui.Screen));

        var textInput = TestUi.Find(ui.Screen, p => p is TextInput) as TextInput;
        Assert.NotNull(textInput);
        textInput.SetValue("second");
        ui.Update();
        ui.Render();

        var rerenderedInput = TestUi.Find(ui.Screen, p => p is TextInput) as TextInput;
        Assert.NotNull(rerenderedInput);
        Assert.Equal("second", rerenderedInput.Value);
        Assert.Contains("second", TestUi.Texts(ui.Screen));
        Assert.Contains("inside card", TestUi.Texts(ui.Screen));
    }

    [Fact]
    public void StatefulChildComponentSurvivesParentRerender()
    {
        using var ui = TestUi.Create();
        ui.RegisterRazorComponent("StatefulChild", """
            <div>child: @count <button @onclick="Increment">increment</button></div>
            @code {
                private int count;
                private void Increment() { count++; }
            }
            """, "StatefulChild");
        ui.LoadRazor("""
            <div>
                <StatefulChild />
                <button @onclick="Refresh">refresh</button>
            </div>
            @code {
                private void Refresh() { StateHasChanged(); }
            }
            """, "StatefulParent");
        ui.Render();

        var childButton = TestUi.FindAll(ui.Screen, p => p is Button)[0];
        ui.ProcessPointerDown(childButton.Layout.X + 1, childButton.Layout.Y + 1);
        ui.ProcessPointerUp(childButton.Layout.X + 1, childButton.Layout.Y + 1);
        ui.Update();
        ui.Render();

        Assert.Contains(TestUi.Texts(ui.Screen), text => text.Contains("child: 1", StringComparison.Ordinal));
    }

    [Fact]
    public void ChildContentDisappearsAndRestores()
    {
        using var ui = TestUi.Create();
        ui.RegisterRazorComponent("ToggleCard", """
            <div class="card"><div class="inner">@ChildContent</div></div>
            @code {
                [Microsoft.AspNetCore.Components.Parameter]
                public Microsoft.AspNetCore.Components.RenderFragment? ChildContent { get; set; }
            }
            """, "ToggleCard");
        ui.LoadRazor("""
            <div class="root">
              <button @onclick="Toggle">Toggle</button>
              @if (show) {
                <ToggleCard>
                  <span class="card-title">Card title</span>
                </ToggleCard>
              }
            </div>
            @code {
                private bool show = true;
                private void Toggle() { show = !show; StateHasChanged(); }
            }
            """, "ToggleDemo");
        ui.Render();
        Assert.Contains("Card title", TestUi.Texts(ui.Screen));

        var button = TestUi.Find(ui.Screen, p => p is Button);
        ui.ProcessPointerDown(button.Layout.X + 1, button.Layout.Y + 1);
        ui.ProcessPointerUp(button.Layout.X + 1, button.Layout.Y + 1);
        ui.Update();
        ui.Render();
        Assert.DoesNotContain("Card title", TestUi.Texts(ui.Screen));

        button = TestUi.Find(ui.Screen, p => p is Button);
        ui.ProcessPointerDown(button.Layout.X + 1, button.Layout.Y + 1);
        ui.ProcessPointerUp(button.Layout.X + 1, button.Layout.Y + 1);
        ui.Update();
        ui.Render();
        Assert.Contains("Card title", TestUi.Texts(ui.Screen));
    }
}
