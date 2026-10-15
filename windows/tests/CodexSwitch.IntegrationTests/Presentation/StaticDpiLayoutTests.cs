using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CodexSwitch.App;

namespace CodexSwitch.IntegrationTests.Presentation;

public sealed class StaticDpiLayoutTests
{
    [Theory]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void Static_sections_grow_to_fit_larger_text_without_vertical_clipping(float fontScale)
        => OnSta(() =>
        {
            // No Shown event and no usable services: this test cannot load real
            // authentication, initialize accounts, refresh usage or launch Codex.
            var services = new AppServices(null!, null!, null!, null!, null!, null!, null!, null!, null!);
            using var form = new MainForm(services, false);
            Field<System.Windows.Forms.Timer>(form, "_timer").Stop();
            form.ClientSize = new Size(460, 2200);
            var root = Field<TableLayoutPanel>(form, "_root");
            Field<Label>(form, "_providerStatus").Text = "OpenAI 官方 · 当前连接";
            Field<Label>(form, "_sub2Status").Text = "Sub2API 服务离线 · 自动刷新 1 分钟";
            var sections = new[] { 0, 1, 2, 4 }.Select(row => root.GetControlFromPosition(0, row)!).ToArray();
            var labels = sections.SelectMany(Descendants).OfType<Label>().Where(label => label.Text.Length > 0).ToArray();
            var fonts = new List<Font>();
            try
            {
                foreach (var label in labels)
                {
                    var largerFont = new Font(label.Font.FontFamily, label.Font.SizeInPoints * fontScale,
                        label.Font.Style, GraphicsUnit.Point);
                    fonts.Add(largerFont);
                    label.Font = largerFont;
                }

                // Simulate the larger glyphs independently of the layout geometry.
                // A blanket Scale() would expand the hard-coded rows as well and
                // would miss the failure when those rows never received autoscaling.
                for (var pass = 0; pass < 4; pass++) LayoutRecursively(root);

                foreach (var label in labels)
                {
                    // Measure with Label's actual text engine and wrapping flags.
                    // Forcing WordBreak through TextRenderer can add a second line
                    // at a near-exact fit even when this Label renders one line
                    // (especially with compatible GDI+ text rendering).
                    var measured = label.GetPreferredSize(new Size(Math.Max(1, label.Width), 0));
                    Assert.True(label.Height >= label.Font.Height + label.Padding.Vertical,
                        $"Single glyph line clipped: '{label.Text}', label={label.Bounds}, fontHeight={label.Font.Height}");
                    Assert.True(label.Height >= measured.Height,
                        $"Text height clipped: '{label.Text}', label={label.Bounds}, measured={measured}, font={label.Font}");

                    var top = label.Top;
                    for (var parent = label.Parent; parent is not null && parent != root; parent = parent.Parent)
                    {
                        Assert.True(top >= 0 && top + label.Height <= parent.ClientSize.Height + 1,
                            $"Ancestor clips '{label.Text}': top={top}, labelHeight={label.Height}, " +
                            $"ancestor={parent.GetType().Name}, bounds={parent.Bounds}, client={parent.ClientSize}");
                        top += parent.Top;
                    }
                }
            }
            finally
            {
                form.Dispose();
                foreach (var font in fonts) font.Dispose();
            }
        });

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void LayoutRecursively(Control parent)
    {
        foreach (Control child in parent.Controls) LayoutRecursively(child);
        parent.PerformLayout();
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Static layout did not settle within 20 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
