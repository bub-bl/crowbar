using Crowbar.UI;

namespace Crowbar.UI.Tests.Razor;

public class RazorFragmentTests
{
    [Fact]
    public void MultipleNamedFragmentsRenderInDeclarationOrder()
    {
        using var ui = TestUi.Create();
        ui.RegisterRazorComponent("Window", """
            <div class="window">
                @if (Header is not null)
                {
                    <div class="window-header">@Header</div>
                }

                <div class="window-body">@Body</div>

                @if (Footer is not null)
                {
                    <div class="window-footer">@Footer</div>
                }
            </div>
            @code {
                [Microsoft.AspNetCore.Components.Parameter]
                public Microsoft.AspNetCore.Components.RenderFragment? Header { get; set; }
                [Microsoft.AspNetCore.Components.Parameter]
                public Microsoft.AspNetCore.Components.RenderFragment? Body { get; set; }
                [Microsoft.AspNetCore.Components.Parameter]
                public Microsoft.AspNetCore.Components.RenderFragment? Footer { get; set; }
            }
            """, "Window");
        ui.LoadRazor("""
            <div class="root">
                <button @onclick="ToggleHeader">Toggle</button>
                <Window>
                    @if (showHeader)
                    {
                        <Header><span class="win-title">Title @name</span></Header>
                    }

                    <Body>
                        <span>Body content</span>
                        <input value="@name" @bind-value="name" />
                    </Body>

                    <Footer><span class="win-foot">Footer @name</span></Footer>
                </Window>
            </div>
            @code {
                private string name = "first";
                private bool showHeader = true;
                private void ToggleHeader() { showHeader = !showHeader; StateHasChanged(); }
            }
            """, "MultiFragmentDemo");
        ui.Prepare();

        var texts = TestUi.Texts(ui.Screen);
        Assert.Contains("Title first", texts);
        Assert.Contains("Body content", texts);
        Assert.Contains("Footer first", texts);
        Assert.True(texts.IndexOf("Title first") < texts.IndexOf("Body content"));
        Assert.True(texts.IndexOf("Body content") < texts.IndexOf("Footer first"));

        var textInput = TestUi.Find(ui.Screen, p => p is TextInput) as TextInput;
        textInput!.SetValue("second");
        ui.Update();
        ui.Prepare();
        texts = TestUi.Texts(ui.Screen);
        Assert.Contains("Title second", texts);
        Assert.Contains("Footer second", texts);

        // Removing a leading region shifts sibling keys; the input must keep its value.
        var button = TestUi.Find(ui.Screen, p => p is Button);
        ui.ProcessPointerDown(button.Layout.X + 1, button.Layout.Y + 1);
        ui.ProcessPointerUp(button.Layout.X + 1, button.Layout.Y + 1);
        ui.Update();
        ui.Prepare();

        Assert.DoesNotContain("Title second", TestUi.Texts(ui.Screen));
        var shiftedInput = TestUi.Find(ui.Screen, p => p is TextInput) as TextInput;
        Assert.NotNull(shiftedInput);
        Assert.Equal("second", shiftedInput.Value);
        Assert.Contains("Body content", TestUi.Texts(ui.Screen));
        Assert.Contains("Footer second", TestUi.Texts(ui.Screen));

        // Re-adding the region restores it.
        button = TestUi.Find(ui.Screen, p => p is Button);
        ui.ProcessPointerDown(button.Layout.X + 1, button.Layout.Y + 1);
        ui.ProcessPointerUp(button.Layout.X + 1, button.Layout.Y + 1);
        ui.Update();
        ui.Prepare();
        Assert.Contains("Title second", TestUi.Texts(ui.Screen));
    }

    [Fact]
    public void MixedChildContentAndNamedFragmentsFollowComponentOrder()
    {
        using var ui = TestUi.Create();
        ui.RegisterRazorComponent("Layout", """
            <div class="layout">
                <div class="layout-head">@Title</div>
                <div class="layout-body">@ChildContent</div>
                <div class="layout-foot">@Footer</div>
            </div>
            @code {
                [Microsoft.AspNetCore.Components.Parameter]
                public Microsoft.AspNetCore.Components.RenderFragment? Title { get; set; }
                [Microsoft.AspNetCore.Components.Parameter]
                public Microsoft.AspNetCore.Components.RenderFragment? ChildContent { get; set; }
                [Microsoft.AspNetCore.Components.Parameter]
                public Microsoft.AspNetCore.Components.RenderFragment? Footer { get; set; }
            }
            """, "Layout");
        ui.LoadRazor("""
            <div class="root">
                <Layout>
                    <Footer><span class="lf">Tail</span></Footer>
                    <span class="cc">Middle</span>
                    <Title><span class="lt">Head</span></Title>
                </Layout>
            </div>
            """, "MixedFragmentsDemo");
        ui.Prepare();

        var texts = TestUi.Texts(ui.Screen);
        Assert.Contains("Head", texts);
        Assert.Contains("Middle", texts);
        Assert.Contains("Tail", texts);
        Assert.True(texts.IndexOf("Head") < texts.IndexOf("Middle"));
        Assert.True(texts.IndexOf("Middle") < texts.IndexOf("Tail"));
    }

    [Fact]
    public void ConditionalSiblingShiftDoesNotMixUpComponentInstances()
    {
        using var ui = TestUi.Create();
        ui.RegisterRazorComponent("MyButton", """
            <div class="btn"><span>@Value</span></div>
            @code {
                [Microsoft.AspNetCore.Components.Parameter] public string Value { get; set; } = string.Empty;
                protected override int BuildHash() => HashCode.Combine(Value);
            }
            """, "MyButton");
        ui.RegisterRazorComponent("Card", """
            <div class="card"><div class="inner">@ChildContent</div></div>
            @code {
                [Microsoft.AspNetCore.Components.Parameter]
                public Microsoft.AspNetCore.Components.RenderFragment? ChildContent { get; set; }
            }
            """, "Card");
        ui.LoadRazor("""
            <div class="root">
                <button @onclick="Toggle">Toggle</button>
                @if (show) { <label class="extra">Extra node</label> }
                <MyButton Value="first" />
                <MyButton Value="second" />
                <MyButton Value="third" />
                <Card><span class="card-title">Card content</span></Card>
            </div>
            @code {
                private bool show = true;
                private void Toggle() { show = !show; StateHasChanged(); }
            }
            """, "PositionShiftDemo");
        ui.Prepare();

        foreach (var extraVisible in new[] { true, false, true })
        {
            Assert.Equal(extraVisible, TestUi.Find(ui.Screen, p => p.Text == "Extra node") is not null);
            Assert.NotNull(TestUi.Find(ui.Screen, p => p.Text == "first"));
            Assert.NotNull(TestUi.Find(ui.Screen, p => p.Text == "third"));
            Assert.NotNull(TestUi.Find(ui.Screen, p => p.Text == "Card content"));
            var button = TestUi.Find(ui.Screen, p => p is Button);
            ui.ProcessPointerDown(button.Layout.X + 1, button.Layout.Y + 1);
            ui.ProcessPointerUp(button.Layout.X + 1, button.Layout.Y + 1);
            ui.Update();
            ui.Prepare();
        }
    }
}
