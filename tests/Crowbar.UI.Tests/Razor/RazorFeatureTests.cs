using Crowbar.UI;

namespace Crowbar.UI.Tests.Razor;

public class RazorFeatureTests
{
    [Fact]
    public void KeyPreservesInputStateAcrossSiblingInsertion()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="root">
              @if (inserted) { <label>Inserted above</label> }
              <input @key="kept" value="@inputValue" @bind-value="inputValue" />
              <button @onclick="Insert">insert</button>
            </div>
            @code {
                private string inputValue = "hello";
                private bool inserted;
                private void Insert() { inserted = true; StateHasChanged(); }
            }
            """, "KeyDemo");
        ui.Render();
        var input = TestUi.Find(ui.Screen, p => p is TextInput) as TextInput;
        Assert.NotNull(input);
        input.SetValue("edited");
        ui.Update();
        ui.Render();

        // Insert a sibling ABOVE the keyed input via a re-render: without
        // @key the input's positional path would shift and its value would be
        // lost; the key keeps the identity stable.
        var button = TestUi.Find(ui.Screen, p => p is Button);
        Assert.NotNull(button);
        ui.ProcessPointerDown(button!.Layout.X + 1, button.Layout.Y + 1);
        ui.ProcessPointerUp(button.Layout.X + 1, button.Layout.Y + 1);
        ui.Update();
        ui.Render();
        var rerendered = TestUi.Find(ui.Screen, p => p is TextInput) as TextInput;
        Assert.NotNull(rerendered);
        Assert.Equal("edited", rerendered.Value);
    }

    [Fact]
    public void RefAssignsPanelsAndComponents()
    {
        using var ui = TestUi.Create();
        ui.RegisterRazorComponent("PlainCard", """
            <div class="card"><span>card</span></div>
            """, "PlainCard");
        ui.LoadRazor("""
            <div class="root">
              <div @ref="boxRef" class="box"></div>
              <PlainCard @ref="cardRef" />
              <button @onclick="Refresh">refresh</button>
              <label>@boxSet @cardType</label>
            </div>
            @code {
                private Panel? boxRef;
                private Panel? cardRef;
                private string boxSet => boxRef is null ? "unset" : "set";
                private string cardType => cardRef is null ? "none" : cardRef.GetType().Name;
                private void Refresh() { StateHasChanged(); }
            }
            """, "RefDemo");
        ui.Render();
        // @ref fields are assigned after the render pass, so they are visible
        // from the next re-render (mirroring Blazor's OnAfterRender timing).
        Assert.NotNull(TestUi.Find(ui.Screen, p => p.Classes.Contains("box")));
        var button = TestUi.Find(ui.Screen, p => p is Button);
        Assert.NotNull(button);
        ui.ProcessPointerDown(button!.Layout.X + 1, button.Layout.Y + 1);
        ui.ProcessPointerUp(button.Layout.X + 1, button.Layout.Y + 1);
        ui.Update();
        ui.Render();
        var texts = TestUi.Texts(ui.Screen);
        Assert.Contains(texts, t => t.Contains("set Template"));
    }

    [Fact]
    public void AttributesSplatsADictionaryOntoAnElement()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="root">
              <div @attributes="attrs" data-marked="yes">splat</div>
            </div>
            @code {
                private readonly Dictionary<string, object> attrs = new()
                {
                    ["class"] = "splatted",
                    ["style"] = "width: 120px; height: 40px;",
                    ["id"] = "splat-id",
                    ["data-extra"] = "hello"
                };
            }
            """, "AttributesDemo");
        ui.Render();
        var panel = TestUi.Find(ui.Screen, p => p.Attributes.ContainsKey("data-extra"));
        Assert.NotNull(panel);
        Assert.True(panel!.Classes.Contains("splatted"));
        Assert.Equal("splat-id", panel.Id);
        Assert.Equal(CssLength.Points(120), panel.ComputedStyle.Width);
        Assert.Equal("hello", panel.Attributes["data-extra"]);
        Assert.True(panel.Attributes.ContainsKey("data-marked"));
    }

    [Fact]
    public void TypeParamComponentClosesGenericFromUsage()
    {
        using var ui = TestUi.Create();
        ui.RegisterRazorComponent("Badge", """
            @typeparam T
            <span>@Value</span>
            @code {
                [Microsoft.AspNetCore.Components.Parameter] public T Value { get; set; } = default!;
            }
            """, "Badge");
        ui.LoadRazor("""
            <div class="root">
              <Badge T="int" Value="42" />
              <Badge T="string" Value="generic text" />
            </div>
            """, "TypeParamDemo");
        ui.Render();
        Assert.Contains("42", TestUi.Texts(ui.Screen));
        Assert.Contains("generic text", TestUi.Texts(ui.Screen));
    }

    [Fact]
    public void KeyboardMouseAndWheelEventsReachHandlers()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="root" style="width: 640px; height: 480px;" @onkeydown="KeyDown" @onmousemove="MouseMove" @onwheel="Wheel" @onfocus="Focus" @onblur="Blur">
              <button class="target">target</button>
              key: @keyCount mouse: @mouseCount wheel: @wheelCount focus: @focusCount blur: @blurCount
            </div>
            @code {
                private int keyCount;
                private int mouseCount;
                private int wheelCount;
                private int focusCount;
                private int blurCount;
                private void KeyDown(Crowbar.UI.KeyEvent e) { keyCount++; StateHasChanged(); }
                private void MouseMove(Crowbar.UI.UiPointerEvent e) { mouseCount++; StateHasChanged(); }
                private void Wheel(Crowbar.UI.WheelEvent e) { wheelCount++; StateHasChanged(); }
                private void Focus() { focusCount++; StateHasChanged(); }
                private void Blur() { blurCount++; StateHasChanged(); }
            }
            """, "EventsDemo");
        ui.Render();

        // Focus the root by clicking an empty spot, then interact.
        ui.ProcessPointerDown(600, 400);
        ui.ProcessPointerUp(600, 400);
        ui.ProcessKey(new KeyEvent(0x41, true, false)); // 'A' down
        ui.ProcessPointerWheel(600, 400, 0, 1);
        // Clicking the button moves focus away (blur) and moves the mouse.
        var button = TestUi.Find(ui.Screen, p => p is Button);
        Assert.NotNull(button);
        ui.ProcessPointerDown(button!.Layout.X + 1, button.Layout.Y + 1);
        ui.ProcessPointerUp(button.Layout.X + 1, button.Layout.Y + 1);
        ui.Update();
        ui.Render();

        var texts = TestUi.Texts(ui.Screen);
        Assert.Contains(texts, t => t.Contains("key: 1") && t.Contains("wheel: 1"));
        Assert.Contains(texts, t => t.Contains("focus: 1") && t.Contains("blur: 1"));
        Assert.Contains(texts, t => t.Contains("mouse:") && !t.Contains("mouse: 0"));
    }

    [Fact]
    public void InlineLambdaAssignmentMutatesState()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="root">
              <button @onclick="() => count++">@count</button>
            </div>
            @code {
                private int count;
            }
            """, "LambdaDemo");
        ui.Render();
        var button = TestUi.Find(ui.Screen, p => p is Button);
        ui.ProcessPointerDown(button!.Layout.X + 1, button.Layout.Y + 1);
        ui.ProcessPointerUp(button.Layout.X + 1, button.Layout.Y + 1);
        ui.Update();
        ui.Render();
        Assert.Contains("1", TestUi.Texts(ui.Screen));
    }

    [Fact]
    public void AnchorNavigatesThroughTheRouter()
    {
        using var ui = TestUi.Create();
        using var dir = TestUi.TempDir("pages");
        var home = dir.Write("Home.razor", "@page \"/\"\n<span>home page</span>");
        var other = dir.Write("Other.razor", "@page \"/other\"\n<span>other page</span>");
        ui.RegisterRazorComponentFromFile("Home", home, "Home");
        ui.RegisterRazorComponentFromFile("Other", other, "Other");
        ui.LoadRazor("""
            <div class="root"><a href="/other">go</a></div>
            """, "AnchorDemo");
        ui.Render();
        var anchor = TestUi.Find(ui.Screen, p => p.TagName == "a");
        Assert.NotNull(anchor);
        ui.ProcessPointerDown(anchor!.Layout.X + 1, anchor.Layout.Y + 1);
        ui.ProcessPointerUp(anchor.Layout.X + 1, anchor.Layout.Y + 1);
        ui.Render();
        Assert.Equal("/other", ui.CurrentUrl);
        Assert.Contains("other page", TestUi.Texts(ui.Screen));
    }

    [Fact]
    public void CheckboxAndRadioToggleAndGroup()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="root">
              <input type="checkbox" checked="@checkedState" @onchange="ToggleChecked" /> check
              <input type="radio" name="g" /> one
              <input type="radio" name="g" /> two
            </div>
            @code {
                private bool checkedState;
                private void ToggleChecked(bool value) { checkedState = value; }
            }
            """, "ToggleDemo");
        ui.Render();
        var toggles = TestUi.FindAll(ui.Screen, p => p is ToggleInput).Cast<ToggleInput>().ToList();
        Assert.Equal(3, toggles.Count);
        Assert.False(toggles[0].IsChecked);

        ui.ProcessPointerDown(toggles[0].Layout.X + 1, toggles[0].Layout.Y + 1);
        ui.ProcessPointerUp(toggles[0].Layout.X + 1, toggles[0].Layout.Y + 1);
        ui.Update();
        ui.Render();
        // The @onchange handler re-renders the tree; re-fetch the fresh panels
        // (the bound `checked` attribute keeps the state across re-renders).
        toggles = TestUi.FindAll(ui.Screen, p => p is ToggleInput).Cast<ToggleInput>().ToList();
        Assert.True(toggles[0].IsChecked);

        // Radios in the same group are mutually exclusive: checking the second
        // radio unchecks the first.
        ui.ProcessPointerDown(toggles[1].Layout.X + 1, toggles[1].Layout.Y + 1);
        ui.ProcessPointerUp(toggles[1].Layout.X + 1, toggles[1].Layout.Y + 1);
        ui.ProcessPointerDown(toggles[2].Layout.X + 1, toggles[2].Layout.Y + 1);
        ui.ProcessPointerUp(toggles[2].Layout.X + 1, toggles[2].Layout.Y + 1);
        ui.Render();
        toggles = TestUi.FindAll(ui.Screen, p => p is ToggleInput).Cast<ToggleInput>().ToList();
        Assert.True(toggles[2].IsChecked);
        Assert.False(toggles[1].IsChecked);
        Assert.True(toggles[0].IsChecked);
    }

    [Fact]
    public void DisabledAndCheckedAttributesWirePanelState()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="root">
              <input type="checkbox" checked="checked" disabled="disabled" />
            </div>
            """, "DisabledDemo");
        ui.Render();
        var toggle = TestUi.Find(ui.Screen, p => p is ToggleInput) as ToggleInput;
        Assert.NotNull(toggle);
        Assert.True(toggle!.IsChecked);
        Assert.False(toggle.IsEnabled);
    }

    [Fact]
    public void DoubleClickAndScrollEventsFire()
    {
        using var ui = TestUi.Create();
        ui.LoadRazor("""
            <div class="root">
              <div style="width: 200px; height: 120px; overflow: auto;" @ondblclick="Dbl" @onscroll="Scr">
                  <div style="width: 200px; height: 400px; flex-shrink: 0;">content</div>
              </div>
              <label>dbl: @dblCount scroll: @scrollCount</label>
            </div>
            @code {
                private int dblCount;
                private int scrollCount;
                private void Dbl(Crowbar.UI.UiPointerEvent e) { dblCount++; StateHasChanged(); }
                private void Scr() { scrollCount++; StateHasChanged(); }
            }
            """, "MoreEventsDemo");
        ui.Render();
        var scroller = TestUi.Find(ui.Screen, p => p.IsScrollContainer);
        Assert.NotNull(scroller);
        var x = scroller!.Layout.X + 5;
        var y = scroller.Layout.Y + 5;
        ui.ProcessPointerDown(x, y);
        ui.ProcessPointerUp(x, y);
        ui.ProcessPointerDown(x, y);
        ui.ProcessPointerUp(x, y);
        ui.Update();
        ui.Render();
        // The dblclick handler re-renders; scroll the fresh panel instance so
        // the @onscroll handler (wired on the new tree) receives the event, then
        // let the StateHasChanged from the handler trigger the re-render.
        scroller = TestUi.Find(ui.Screen, p => p.IsScrollContainer);
        Assert.NotNull(scroller);
        Assert.True(scroller!.MaxScrollY > 0);
        scroller.ScrollTo(0, 100);
        ui.Update();
        ui.Render();
        var texts = TestUi.Texts(ui.Screen);
        Assert.Contains(texts, t => t.Contains("dbl: 1") && t.Contains("scroll: 1"));
    }
}
