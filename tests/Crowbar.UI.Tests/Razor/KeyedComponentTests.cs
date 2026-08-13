using Crowbar.UI;

namespace Crowbar.UI.Tests.Razor;

public class KeyedComponentTests
{
    [Fact]
    public void KeyedComponentsKeepStateAcrossPositionChanges()
    {
        using var ui = TestUi.Create();
        ui.RegisterRazorComponent("KeyedCounter", """
            <div class="kp"><span>@Label</span><button @onclick="Bump">@count</button></div>
            @code {
                [Microsoft.AspNetCore.Components.Parameter] public string Label { get; set; } = string.Empty;
                private int count;
                private void Bump() { count++; }
            }
            """, "KeyedCounter");
        ui.LoadRazor("""
            <div class="root">
                <button @onclick="Swap">swap</button>
                @if (first)
                {
                    <KeyedCounter @key="a" Label="A" />
                    <KeyedCounter @key="b" Label="B" />
                }
                else
                {
                    <KeyedCounter @key="b" Label="B" />
                    <KeyedCounter @key="a" Label="A" />
                }
            </div>
            @code {
                private bool first = true;
                private void Swap() { first = !first; StateHasChanged(); }
            }
            """, "KeyDemo");
        ui.Prepare();

        // Increment the counter labelled A (second button on the first pass).
        var buttons = TestUi.FindAll(ui.Screen, p => p is Button);
        Assert.Equal(3, buttons.Count);
        var aButton = buttons[1];
        ui.ProcessPointerDown(aButton.Layout.X + 1, aButton.Layout.Y + 1);
        ui.ProcessPointerUp(aButton.Layout.X + 1, aButton.Layout.Y + 1);
        ui.Update();
        ui.Prepare();
        Assert.Contains(TestUi.Texts(ui.Screen), text => text == "1");

        // Swap the order of the two keyed components: their instances (and
        // therefore their counters) must survive the position change.
        var swap = TestUi.Find(ui.Screen, p => p is Button);
        ui.ProcessPointerDown(swap.Layout.X + 1, swap.Layout.Y + 1);
        ui.ProcessPointerUp(swap.Layout.X + 1, swap.Layout.Y + 1);
        ui.Update();
        ui.Prepare();

        Assert.Contains(TestUi.Texts(ui.Screen), text => text == "1"); // A kept its count
        Assert.Contains(TestUi.Texts(ui.Screen), text => text == "0"); // B still untouched
    }
}
