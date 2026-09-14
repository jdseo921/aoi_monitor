using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AOI_Monitor.Services;
using AOI_Monitor.ViewModels;
using Xunit;

namespace AOI_Monitor.UiTests;

public sealed class MainWindowLoadingOverlayTests
{
    [Fact]
    public void FastAsyncNavigationDoesNotLeaveDelayedLoadingOverlayVisible()
    {
        RunOnSta(() =>
        {
            EnsureApplicationResources();
            var window = new MainWindow();
            try
            {
                var lifecycleTask = InvokeRunPageLifecycleAsync(window, new FastAsyncPage());
                WaitForDispatcherTask(lifecycleTask, TimeSpan.FromSeconds(2), "Fast page navigation lifecycle did not complete.");
                PumpDispatcher(TimeSpan.FromMilliseconds(250));

                var overlay = GetNamedElement<FrameworkElement>(window, "LoadingOverlay");
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void HomeKeepsWorkflowNavigationItemsInLargeModuleMap()
    {
        RunOnSta(() =>
        {
            EnsureApplicationResources();
            var viewModel = new MainViewModel();
            var home = new AOI_Monitor.Views.HomeView { DataContext = viewModel };
            home.Measure(new Size(1920, 1080));
            home.Arrange(new Rect(0, 0, 1920, 1080));
            home.UpdateLayout();

            var navItems = GetNamedElement<ItemsControl>(home, "HomeModuleItems");
            var panel = Assert.IsType<UniformGrid>(navItems.ItemsPanel.LoadContent());
            var keys = viewModel.NavPages.Select(page => page.Key).ToArray();

            Assert.Equal(13, viewModel.NavPages.Count);
            Assert.Equal(4, panel.Columns);
            Assert.Equal(
                new[]
                {
                    "library",
                    "monitor",
                    "compare",
                    "review",
                    "recipe",
                    "modeltest",
                    "spc",
                    "reports",
                    "readiness",
                    "calibration",
                    "profile",
                    "pilot",
                    "settings",
                },
                keys);
        });
    }

    [Fact]
    public void ShellRouteMapConstructsEveryWorkflowAndSupportPage()
    {
        RunOnSta(() =>
        {
            EnsureApplicationResources();
            var window = new MainWindow();
            try
            {
                var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
                var viewModelField = typeof(MainWindow).GetField("_vm", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("_vm field was not found.");
                viewModelField.SetValue(window, viewModel);

                var createPage = typeof(MainWindow).GetMethod("CreatePage", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("CreatePage was not found.");
                var keys = viewModel.NavPages
                    .Select(page => page.Key)
                    .Concat(new[] { "home", "install", "guide" })
                    .ToArray();

                foreach (var key in keys)
                {
                    var page = Assert.IsAssignableFrom<UserControl>(createPage.Invoke(window, new object[] { key }));
                    Assert.NotNull(page);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ShellHomeButtonReturnsWorkflowToHome()
    {
        RunOnSta(() =>
        {
            EnsureApplicationResources();
            var window = new MainWindow();
            try
            {
                var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
                var viewModelField = typeof(MainWindow).GetField("_vm", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("_vm field was not found.");
                viewModelField.SetValue(window, viewModel);
                viewModel.CurrentPage = "compare";

                var homeButton = GetNamedElement<Button>(window, "HomeNavBtn");
                homeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                Assert.Equal("home", viewModel.CurrentPage);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static Task InvokeRunPageLifecycleAsync(MainWindow window, IAsyncNavigationPage page)
    {
        var method = typeof(MainWindow).GetMethod("RunPageLifecycleAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RunPageLifecycleAsync was not found.");
        return (Task?)method.Invoke(window, new object[] { "monitor", page, CancellationToken.None })
            ?? throw new InvalidOperationException("RunPageLifecycleAsync did not return a task.");
    }

    private static void WaitForDispatcherTask(Task task, TimeSpan timeout, string timeoutMessage)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            _ = Task.Delay(timeout).ContinueWith(_ =>
                dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        if (!task.IsCompleted)
            throw new TimeoutException(timeoutMessage);

        task.GetAwaiter().GetResult();
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        _ = Task.Delay(duration).ContinueWith(_ =>
            dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
    }

    private static T GetNamedElement<T>(MainWindow window, string fieldName)
        where T : FrameworkElement
    {
        var field = typeof(MainWindow).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"{fieldName} was not found.");
        return (T)field.GetValue(window)!;
    }

    private static T GetNamedElement<T>(FrameworkElement root, string name)
        where T : FrameworkElement
    {
        return (T)(root.FindName(name)
            ?? throw new InvalidOperationException($"{name} was not found."));
    }

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                ShutdownApplicationIfOwnedByCurrentThread();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
            throw exception;
    }

    private static void EnsureApplicationResources()
    {
        if (Application.Current is not null)
            return;

        var app = new App();
        app.InitializeComponent();
    }

    private static void ShutdownApplicationIfOwnedByCurrentThread()
    {
        try
        {
            if (Application.Current?.Dispatcher.CheckAccess() == true)
                Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Application shutdown cleanup failed: {ex.Message}");
        }
    }

    private sealed class FastAsyncPage : UserControl, IAsyncNavigationPage
    {
        public Task OnNavigatedToAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void CancelWork()
        {
        }
    }
}
