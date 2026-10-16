using System;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProcessesKiller;

// In-process WPF component tests: no desktop input and no real user processes are ended.
internal static class WindowTests
{
    private static int _checks;
    private static MainWindow _window;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    public static int Main(string[] args)
    {
        Console.WriteLine("WPF: initialize application resources");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App();
        app.InitializeComponent();
        // Suppress the startup window queued by Application: this test never shows desktop UI.
        typeof(Application).GetField("_startupUri", Private).SetValue(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Process fixture = null;
        Process otherFixture = null;
        try
        {
            Console.WriteLine("WPF: construct window without showing it");
            _window = new MainWindow();
            PumpUntil(() => !Field<bool>("_isRefreshing"));
            Console.WriteLine("WPF: check process page");
            Check(Control<TabControl>("WorkspaceTabs").SelectedIndex == 0, "default process tab");
            Check(_window.Processes.All(info => new[] { "java", "javaw", "explorer" }.Contains(info.NormalizedName)), "original whitelist");
            Call("ApplyProcessGridSort", "MemoryBytes", ListSortDirection.Descending);
            var view = CollectionViewSource.GetDefaultView(_window.Processes);
            Check(view.SortDescriptions[0].PropertyName == "NormalizedName" && view.SortDescriptions.Any(sort => sort.PropertyName == "MemoryBytes"), "original grouping and sorting");
            RenderLayout(args[0], 1200, 630, "layout-processes.png");
            CheckComboAlignment(Control<ComboBox>("ProcessFilterComboBox"));
            Control<TabControl>("WorkspaceTabs").SelectedIndex = 1;
            Check(!Field<bool>("_hasPortQuery") && _window.PortProcesses.Count == 0, "first tab visit does not query all ports");
            Control<TextBox>("PortInputTextBox").Text = "65536";
            Call("SubmitPortQuery");
            Check(Control<TextBlock>("PortInputErrorTextBlock").Text.Contains("1–65535"), "inline invalid-port message");
            fixture = Process.Start(new ProcessStartInfo(Path.Combine(args[0], "ProcessesKiller.ServiceTests.exe"), "--listener 0")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            var ready = fixture.StandardOutput.ReadLineAsync();
            Check(ready.Wait(10000), "fixture ready");
            var port = int.Parse(ready.Result);
            otherFixture = Process.Start(new ProcessStartInfo(Path.Combine(args[0], "ProcessesKiller.ServiceTests.exe"), "--listener 0")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            var otherReady = otherFixture.StandardOutput.ReadLineAsync();
            Check(otherReady.Wait(10000), "second fixture ready");
            var otherPort = int.Parse(otherReady.Result);
            Control<TextBox>("PortInputTextBox").Text = string.Empty;
            Call("SubmitPortQuery"); WaitPort();
            Check(Field<bool>("_hasPortQuery") && !GetNullablePort().HasValue && Field<string>("_portState").Contains("全部端口"), "empty input starts all-port query");
            Check(_window.PortProcesses.Count(row => row.Id == fixture.Id) == 4 && _window.PortProcesses.Count(row => row.Id == otherFixture.Id) == 4,
                "all-port query shows both fixtures on different ports");
            Control<TextBox>("PortInputTextBox").Text = "   ";
            Call("SubmitPortQuery"); WaitPort();
            Check(Field<bool>("_hasPortQuery") && !GetNullablePort().HasValue && Control<TextBlock>("PortInputErrorTextBlock").Text == string.Empty, "whitespace input queries all");
            Control<ComboBox>("PortProtocolComboBox").SelectedIndex = 1;
            Call("SubmitPortQuery"); WaitPort();
            Check(_window.PortProcesses.All(row => row.Protocol == "TCP") && _window.PortProcesses.Count(row => row.Id == fixture.Id) == 2,
                "all-port TCP filter; selected=" + Control<ComboBox>("PortProtocolComboBox").SelectedIndex + "; committed=" + GetField("_queriedProtocol")
                + "; protocols=" + string.Join(",", _window.PortProcesses.Select(row => row.Protocol).Distinct())
                + "; fixture rows=" + _window.PortProcesses.Count(row => row.Id == fixture.Id) + "; state=" + Field<string>("_portState"));
            Control<ComboBox>("PortProtocolComboBox").SelectedIndex = 2;
            Call("SubmitPortQuery"); WaitPort();
            Check(_window.PortProcesses.All(row => row.Protocol == "UDP") && _window.PortProcesses.Count(row => row.Id == otherFixture.Id) == 2, "all-port UDP filter");
            Control<ComboBox>("PortProtocolComboBox").SelectedIndex = 0;
            Call("SubmitPortQuery"); WaitPort();
            RenderLayout(args[0], 1200, 630, "layout-all-ports.png");
            Call("PortClearButton_Click", null, new RoutedEventArgs());
            RenderLayout(args[0], 1200, 630, "layout-empty.png");
            Control<TextBox>("PortInputTextBox").Text = " " + port + " ";
            Call("SubmitPortQuery");
            WaitPort();
            Check(_window.PortProcesses.Count(row => row.Id == fixture.Id) == 4, "port page sees non-whitelisted fixture");
            var grid = Control<DataGrid>("PortProcessGrid");
            grid.SelectedItem = _window.PortProcesses.First(row => row.Id == fixture.Id);
            Check(Control<Button>("KillPortSelectedButton").IsEnabled, "eligible selection can terminate");
            Check(grid.SelectedItem != null && ((MainWindow.PortProcessRow)grid.SelectedItem).Process.CommandLine.Contains("--listener"), "process details include command line");
            var selected = grid.SelectedItem;
            view = CollectionViewSource.GetDefaultView(_window.PortProcesses);
            Check(string.Join(",", view.SortDescriptions.Select(sort => sort.PropertyName)) == "Name,Id,Port", "default process and port sort");
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription("Protocol", ListSortDirection.Descending));
            Refresh();
            Check(ReferenceEquals(grid.SelectedItem, selected) && view.SortDescriptions.Count == 1, "refresh preserves row selection and sorting");
            Call("SubmitPortQuery"); WaitPort();
            Check(ReferenceEquals(grid.SelectedItem, selected), "same query preserves selection");
            var confirmCount = 0;
            SetConfirmation((message, title) => { confirmCount++; Check(message.Contains(port.ToString()) && message.Contains(fixture.Id.ToString()) && message.Contains("整个进程"), "confirmation describes actual targets"); return false; });
            Call("KillPortSelectedButton_Click", null, new RoutedEventArgs());
            WaitPort();
            Check(confirmCount == 1 && !fixture.HasExited && Field<string>("_portLastResult").Contains("取消"), "cancel leaves fixture alive");
            Control<TabControl>("WorkspaceTabs").SelectedIndex = 0;
            Control<TabControl>("WorkspaceTabs").SelectedIndex = 1;
            WaitPort();
            Check(Field<string>("_portLastResult").Contains("取消") && grid.SelectedItem != null, "tab switch preserves recent result and selection");
            Control<TextBox>("PortInputTextBox").Text = "65536";
            Check(!Control<Button>("KillPortSelectedButton").IsEnabled && Field<bool>("_portConditionsDirty"), "editing conditions disables old results");
            Refresh();
            Check(Field<string>("_portState").Contains("条件已修改"), "dirty conditions do not auto-refresh");
            Control<TextBox>("PortInputTextBox").Text = port.ToString();
            Control<ComboBox>("PortProtocolComboBox").SelectedIndex = 1;
            Call("SubmitPortQuery"); WaitPort();
            Check(_window.PortProcesses.Count == 2 && _window.PortProcesses.All(row => row.Protocol == "TCP"), "UI TCP filter");
            Control<ComboBox>("PortProtocolComboBox").SelectedIndex = 2;
            Call("SubmitPortQuery"); WaitPort();
            Check(_window.PortProcesses.Count == 2 && _window.PortProcesses.All(row => row.Protocol == "UDP"), "UI UDP filter");
            Control<ComboBox>("PortProtocolComboBox").SelectedIndex = 0;
            Call("SubmitPortQuery"); WaitPort();
            RenderLayout(args[0], 1200, 630, "layout-default.png");
            CheckComboAlignment(Control<ComboBox>("PortProtocolComboBox"));
            Check(grid.Columns.Count == 5 && _window.FindName("PortDetailsExpander") == null && _window.FindName("CopyPortInfoButton") == null,
                "compact page has five columns and no permanent details or copy button");
            CheckDetailsDialog(_window.PortProcesses.First(row => row.Id == fixture.Id));
            CheckConfirmationDialog();
            RenderLayout(args[0], 900, 430, "layout-minimum.png");
            Check(grid.ActualHeight >= 200, "minimum window leaves usable results grid");
            var root = (FrameworkElement)_window.Content;
            var killPosition = Control<Button>("KillPortSelectedButton").TransformToAncestor(root).Transform(new Point());
            var autoPosition = Control<CheckBox>("PortAutoRefreshCheckBox").TransformToAncestor(root).Transform(new Point());
            Check(killPosition.X + Control<Button>("KillPortSelectedButton").ActualWidth <= autoPosition.X, "single toolbar does not overlap at minimum width");
            var originalService = GetField("_portQueryService");
            Control<CheckBox>("PortAutoRefreshCheckBox").IsChecked = false;
            Call("RefreshTimer_Tick", null, EventArgs.Empty);
            Check(!Field<bool>("_isQueryingPort"), "disabled auto-refresh does not query");
            SetQueryReader(originalService, false, false, true, port, fixture.Id);
            Refresh();
            Check(_window.PortProcesses.Count == 1 && !Field<bool>("_portQueryComplete") && Field<string>("_portState").Contains("查询不完整"), "partial query preserves known owner and warning");
            grid.SelectedItem = _window.PortProcesses[0];
            Check(((MainWindow.PortProcessRow)grid.SelectedItem).CanTerminate && !Control<Button>("KillPortSelectedButton").IsEnabled, "partial table failure disables termination even for identified owner");
            SetQueryReader(originalService, true, false);
            Refresh();
            Check(!Field<bool>("_portQueryComplete") && Field<string>("_portState").Contains("无法完整确认"), "query failure is not reported as free");
            Check(!Control<Button>("KillPortSelectedButton").IsEnabled, "failed query cannot terminate");
            SetQueryReader(originalService, false, true);
            Control<TextBox>("PortInputTextBox").Text = string.Empty;
            Call("SubmitPortQuery");
            Control<TextBox>("PortInputTextBox").Text = (port + 1).ToString();
            Call("SubmitPortQuery"); WaitPort();
            Check(Field<string>("_portState").Contains((port + 1).ToString()) && _window.PortProcesses.Count == 0, "latest query wins over in-flight old query");
            Call("SubmitPortQuery");
            Call("PortClearButton_Click", null, new RoutedEventArgs()); WaitPort();
            Check(_window.PortProcesses.Count == 0 && !Field<bool>("_hasPortQuery") && !GetNullablePort().HasValue && Field<string>("_portState").Contains("尚未查询"), "clear discards in-flight result");
            Control<CheckBox>("PortAutoRefreshCheckBox").IsChecked = true;
            Call("RefreshTimer_Tick", null, EventArgs.Empty);
            Check(!Field<bool>("_isQueryingPort"), "cleared query does not restart as all ports");
            Control<CheckBox>("PortAutoRefreshCheckBox").IsChecked = false;
            SetField("_portQueryService", originalService);
            Control<TextBox>("PortInputTextBox").Text = string.Empty;
            Call("SubmitPortQuery"); WaitPort();
            grid.SelectedItem = _window.PortProcesses.First(row => row.Id == fixture.Id && row.Protocol == "TCP" && row.IpVersion == "IPv4");
            SetQueryReader(originalService, false, false, false, otherPort, fixture.Id, true);
            SetConfirmation((message, title) => true);
            Call("KillPortSelectedButton_Click", null, new RoutedEventArgs());
            WaitTermination();
            Check(!fixture.HasExited && Field<string>("_portOperationDetails").Contains("选中的端口地址"), "other endpoint on same process cannot justify killing stale selection");
            SetField("_portQueryService", originalService);
            Refresh();
            grid.SelectedItems.Clear();
            foreach (var row in _window.PortProcesses.Where(row => row.Id == fixture.Id).ToList()) grid.SelectedItems.Add(row);
            SetConfirmation((message, title) => true);
            Call("KillPortSelectedButton_Click", null, new RoutedEventArgs());
            WaitTermination();
            Check(fixture.HasExited && !otherFixture.HasExited && _window.PortProcesses.All(row => row.Id != fixture.Id)
                && _window.PortProcesses.Count(row => row.Id == otherFixture.Id) == 4, "all-port termination only ends selected fixture and requeries");
            Check(Field<string>("_portLastResult").Contains("请求 1 个，成功 1 个") && Control<Button>("PortResultDetailsButton").IsEnabled, "deduplicated operation result links to details");
            Control<TextBox>("PortInputTextBox").Text = port.ToString();
            Call("SubmitPortQuery"); WaitPort();
            Check(_window.PortProcesses.Count == 0 && Field<string>("_portState").Contains("已无监听/绑定"), "specific port is empty after selected process exits");
            Control<TabControl>("WorkspaceTabs").SelectedIndex = 0;
            SetConfirmation((message, title) => false);
            var explorer = _window.Processes.FirstOrDefault(info => info.IsExplorer);
            if (explorer != null)
            {
                Call("KillProcesses", (object)new[] { explorer });
                Check(Field<string>("_lastResult").Contains("取消"), "original explorer confirmation remains cancellable");
            }
            var self = new MainWindow.ProcessInfo { Id = Process.GetCurrentProcess().Id, Name = "WindowTests.exe", NormalizedName = "WindowTests" };
            Call("KillProcesses", (object)new[] { self });
            Check(Field<string>("_lastResult").Contains("跳过当前程序"), "original page protects self");
            Control<TabControl>("WorkspaceTabs").SelectedIndex = 1;
            SetQueryReader(originalService, false, true);
            Call("SubmitPortQuery");
            var beforeClose = Control<TextBlock>("PortLastResultTextBlock").Text;
            _window.Close();
            PumpUntil(() => !Field<bool>("_isQueryingPort") && !Field<bool>("_isRefreshing"));
            Check(Field<bool>("_isClosing") && Control<TextBlock>("PortLastResultTextBlock").Text == beforeClose, "closing prevents asynchronous UI writeback");
            Console.WriteLine("PASS: " + _checks + " WPF component assertions; layouts rendered to " + args[0]);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().FullName + ": " + ex.Message); return 1; }
        finally
        {
            if (fixture != null) { if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(3000); } fixture.Dispose(); }
            if (otherFixture != null) { if (!otherFixture.HasExited) { otherFixture.Kill(); otherFixture.WaitForExit(3000); } otherFixture.Dispose(); }
            if (_window != null && !Field<bool>("_isClosing")) _window.Close();
            app.Shutdown();
        }
    }

    private static T Control<T>(string name) where T : class { return (T)_window.FindName(name); }
    private static object GetField(string name) { return typeof(MainWindow).GetField(name, Private).GetValue(_window); }
    private static T Field<T>(string name) { return (T)GetField(name); }
    private static int? GetNullablePort() { return (int?)GetField("_queriedPort"); }
    private static void SetField(string name, object value) { typeof(MainWindow).GetField(name, Private).SetValue(_window, value); }
    private static object Call(string name, params object[] args) { return typeof(MainWindow).GetMethod(name, Private).Invoke(_window, args); }
    private static void SetConfirmation(Func<string, string, bool> callback) { typeof(MainWindow).GetProperty("TerminationConfirmation", Private).SetValue(_window, callback, null); }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); _checks++; }
    private static void WaitPort() { PumpUntil(() => !Field<bool>("_isQueryingPort")); }
    private static void WaitTermination() { PumpUntil(() => !Field<bool>("_isTerminating") && !Field<bool>("_isConfirmingPortKill") && !Field<bool>("_isQueryingPort")); }
    private static void Refresh() { var task = (System.Threading.Tasks.Task)Call("RefreshPortResultsAsync", false, null); PumpUntil(() => task.IsCompleted && !Field<bool>("_isQueryingPort")); }

    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
            timer.Tick += (sender, args) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
            if (watch.ElapsedMilliseconds > 15000) throw new Exception("Timed out waiting for component operation.");
        } while (!done());
    }

    private static void SetQueryReader(object originalService, bool fail, bool delay, bool partial = false, int port = 8080, int pid = 0, bool completeEndpoint = false)
    {
        var serviceType = originalService.GetType();
        var endpointType = serviceType.Assembly.GetType("ProcessesKiller.PortEndpoint");
        var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(endpointType);
        var tcp = Expression.Parameter(typeof(bool), "tcp");
        var ipv6 = Expression.Parameter(typeof(bool), "ipv6");
        Expression body = Expression.New(listType);
        if (partial || completeEndpoint)
        {
            var endpoints = (IList)Activator.CreateInstance(listType);
            var endpoint = Activator.CreateInstance(endpointType);
            endpointType.GetProperty("Protocol").SetValue(endpoint, "TCP", null);
            endpointType.GetProperty("IpVersion").SetValue(endpoint, "IPv4", null);
            endpointType.GetProperty("LocalAddress").SetValue(endpoint, "127.0.0.1", null);
            endpointType.GetProperty("Port").SetValue(endpoint, port, null);
            endpointType.GetProperty("ProcessId").SetValue(endpoint, pid, null);
            endpoints.Add(endpoint);
            body = partial ? (Expression)Expression.Condition(Expression.AndAlso(Expression.Not(tcp), ipv6),
                Expression.Throw(Expression.Constant(new Win32Exception(5)), listType), Expression.Constant(endpoints, listType)) : Expression.Constant(endpoints, listType);
        }
        if (fail) body = Expression.Throw(Expression.Constant(new Win32Exception(5)), listType);
        else if (delay) body = Expression.Block(Expression.Call(typeof(Thread).GetMethod("Sleep", new[] { typeof(int) }), Expression.Constant(100)), body);
        var callback = Expression.Lambda(typeof(Func<,,>).MakeGenericType(typeof(bool), typeof(bool), listType), body, tcp, ipv6).Compile();
        var service = Activator.CreateInstance(serviceType, Private, null, new object[] { callback }, null);
        SetField("_portQueryService", service);
    }

    private static void CheckComboAlignment(ComboBox combo)
    {
        var presenter = (ContentPresenter)combo.Template.FindName("contentPresenter", combo);
        var origin = presenter.TransformToAncestor(combo).Transform(new Point());
        Check(Math.Abs(origin.Y + presenter.ActualHeight / 2 - combo.ActualHeight / 2) <= 1.0,
            combo.Name + " selected text is vertically centered");
        Check(combo.HorizontalContentAlignment == HorizontalAlignment.Left, combo.Name + " selected text stays left aligned");
        var option = new ComboBoxItem { Content = "全部", Style = combo.ItemContainerStyle };
        option.Measure(new Size(180, 32));
        option.Arrange(new Rect(0, 0, 180, 32));
        option.UpdateLayout();
        var optionPresenter = Descendant<ContentPresenter>(option);
        var optionOrigin = optionPresenter.TransformToAncestor(option).Transform(new Point());
        Check(Math.Abs(optionOrigin.Y + optionPresenter.ActualHeight / 2 - option.ActualHeight / 2) <= 1.0
            && option.HorizontalContentAlignment == HorizontalAlignment.Left, combo.Name + " drop-down option alignment");
    }

    private static T Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T) return (T)child;
            var match = Descendant<T>(child);
            if (match != null) return match;
        }
        return null;
    }

    private static void CheckDetailsDialog(MainWindow.PortProcessRow row)
    {
        var details = (Window)Call("CreatePortDetailsDialog", row);
        var text = ((Grid)details.Content).Children.OfType<TextBox>().Single();
        Check(text.IsReadOnly && text.Text.Contains(row.Process.StartTimeDisplay) && text.Text.Contains(row.Process.ProcessPath)
            && text.Text.Contains("--listener"), "detail dialog displays selectable process metadata");
        details.Close();
    }

    private static void CheckConfirmationDialog()
    {
        var factory = typeof(MainWindow).Assembly.GetType("ProcessesKiller.ProcessDialogs").GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic);
        var dialog = (Window)factory.Invoke(null, new object[] { _window, "确认结束端口进程", "将结束整个进程", string.Join("\n", Enumerable.Repeat("进程 (PID 1234) TCP 127.0.0.1:8080", 100)), true });
        var content = (Grid)dialog.Content;
        content.Measure(new Size(660, 380));
        content.Arrange(new Rect(0, 0, 660, 380));
        content.UpdateLayout();
        var text = content.Children.OfType<TextBox>().Single();
        Check(text.IsReadOnly && text.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && text.ActualHeight < 350,
            "large target list remains bounded and scrollable");
        var buttons = content.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToList();
        Check(buttons.Single(button => button.IsCancel).IsDefault && !buttons.Single(button => !button.IsCancel).IsDefault,
            "confirmation defaults to cancel");
        dialog.Close();
    }

    private static void RenderLayout(string directory, int width, int height, string file)
    {
        var content = (FrameworkElement)_window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        PumpUntil(() => true);
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(directory, file))) encoder.Save(stream);
    }
}
