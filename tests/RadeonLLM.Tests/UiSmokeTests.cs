using System.Windows;
using System.Windows.Controls;
using RadeonLLM.App;
using Xunit;

namespace RadeonLLM.Tests;

[Collection("env")]
public class UiSmokeTests
{
    static void Sta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (err is not null) throw new Exception("UI test failed: " + err, err);
    }

    [Fact]
    public void MainWindowBuildsRefreshesAndSwitchesAllTabs()
    {
        var home = Directory.CreateTempSubdirectory().FullName;
        Environment.SetEnvironmentVariable("RADEONLLM_HOME", home);
        Sta(() =>
        {
            var app = new RadeonLLM.App.App();
            app.InitializeComponent();
            using var c = new AppController();
            var w = new MainWindow(c);
            w.Show();
            var tabs = (TabControl)w.FindName("Tabs");
            Assert.Equal(5, tabs.Items.Count);
            for (int i = 0; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                w.UpdateLayout();
            }
            // Fresh install: setup panel visible, RUN disabled (no runtime, no model).
            Assert.Equal(Visibility.Visible, ((FrameworkElement)w.FindName("SetupPanel")).Visibility);
            Assert.False(((Button)w.FindName("RunBtn")).IsEnabled);
            w.Close();
        });
    }
}
