using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ProcessesKiller
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string TargetProcessNamesSetting = "TargetProcessNames";
        private const string DefaultTargetProcessNames = "java,javaw,explorer";
        private const string MissingText = "无权限/不可用";
        private const string AllProcessFilterName = "";
        private const string AllProcessFilterDisplayName = "全部";
        private static readonly string[] FallbackProcessColors =
        {
            "#7C3AED",
            "#BE123C",
            "#0F766E",
            "#A16207",
            "#4338CA",
            "#0369A1"
        };

        private readonly ObservableCollection<ProcessInfo> _processes = new ObservableCollection<ProcessInfo>();
        private readonly ObservableCollection<ProcessFilterOption> _processFilterOptions = new ObservableCollection<ProcessFilterOption>();
        private readonly DispatcherTimer _refreshTimer = new DispatcherTimer();
        private readonly int _currentProcessId = Process.GetCurrentProcess().Id;
        private HashSet<string> _targetProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _selectedProcessNameFilter = AllProcessFilterName;
        private bool _isUpdatingFilterOptions;
        private bool _isRefreshing;
        private bool _refreshRequested;
        private string _pendingRefreshResultText;
        private string _lastResult = "就绪";

        internal enum ExplorerProcessKind
        {
            None,
            Shell,
            Additional,
            Unknown
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            EnableLiveSorting();
            RefreshProcesses("就绪");
            _refreshTimer.Interval = TimeSpan.FromSeconds(3);
            _refreshTimer.Tick += RefreshTimer_Tick;
            _refreshTimer.Start();
        }

        public ObservableCollection<ProcessInfo> Processes
        {
            get { return _processes; }
        }

        public ObservableCollection<ProcessFilterOption> ProcessFilterOptions
        {
            get { return _processFilterOptions; }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshProcesses("已刷新。");
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            RefreshProcesses(null);
        }

        private void ProcessFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingFilterOptions)
            {
                return;
            }

            var selectedOption = ProcessFilterComboBox.SelectedItem as ProcessFilterOption;
            _selectedProcessNameFilter = selectedOption == null ? AllProcessFilterName : selectedOption.NormalizedName;
            RefreshProcesses("已筛选：" + (selectedOption == null ? AllProcessFilterDisplayName : selectedOption.DisplayName));
        }

        private void KillSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedProcesses = ProcessGrid.SelectedItems.Cast<ProcessInfo>().ToList();
            KillProcesses(selectedProcesses);
        }

        private void KillAllButton_Click(object sender, RoutedEventArgs e)
        {
            KillProcesses(_processes.ToList());
        }

        private void KillSingleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selectedProcess = ProcessGrid.SelectedItem as ProcessInfo;
            if (selectedProcess == null)
            {
                UpdateStatus("请先选择一个进程。");
                return;
            }

            KillProcesses(new[] { selectedProcess });
        }

        private void ProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateStatus(null);
        }

        private void ProcessGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row == null)
            {
                return;
            }

            ProcessGrid.SelectedItems.Clear();
            row.IsSelected = true;
            ProcessGrid.CurrentItem = row.Item;
        }

        private void ProcessGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            var sortPropertyName = GetColumnSortProperty(e.Column);
            if (string.IsNullOrWhiteSpace(sortPropertyName))
            {
                return;
            }

            e.Handled = true;

            var sortDirection = e.Column.SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;

            foreach (var column in ProcessGrid.Columns)
            {
                column.SortDirection = null;
            }

            e.Column.SortDirection = sortDirection;
            ApplyProcessGridSort(sortPropertyName, sortDirection);
        }

        private void EnableLiveSorting()
        {
            var liveView = CollectionViewSource.GetDefaultView(_processes) as ICollectionViewLiveShaping;
            if (liveView == null || !liveView.CanChangeLiveSorting)
            {
                return;
            }

            AddLiveSortingProperty(liveView, "NormalizedName");
            AddLiveSortingProperty(liveView, "ExplorerKindSortOrder");
            AddLiveSortingProperty(liveView, "Id");
            AddLiveSortingProperty(liveView, "MemoryBytes");
            AddLiveSortingProperty(liveView, "ThreadCountValue");
            liveView.IsLiveSorting = true;
        }

        private void ApplyProcessGridSort(string sortPropertyName, ListSortDirection sortDirection)
        {
            var view = CollectionViewSource.GetDefaultView(_processes);
            if (view == null)
            {
                return;
            }

            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                AddSortDescription(view.SortDescriptions, "NormalizedName", ListSortDirection.Ascending);
                AddSortDescription(view.SortDescriptions, "ExplorerKindSortOrder", ListSortDirection.Ascending);
                AddSortDescription(view.SortDescriptions, sortPropertyName, sortDirection);
                AddSortDescription(view.SortDescriptions, "Id", ListSortDirection.Ascending);
            }
        }

        private static void AddSortDescription(SortDescriptionCollection sortDescriptions, string propertyName, ListSortDirection sortDirection)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return;
            }

            if (sortDescriptions.Any(sortDescription => string.Equals(sortDescription.PropertyName, propertyName, StringComparison.Ordinal)))
            {
                return;
            }

            sortDescriptions.Add(new SortDescription(propertyName, sortDirection));
        }

        private static string GetColumnSortProperty(DataGridColumn column)
        {
            if (!string.IsNullOrWhiteSpace(column.SortMemberPath))
            {
                return column.SortMemberPath;
            }

            var boundColumn = column as DataGridBoundColumn;
            var binding = boundColumn == null ? null : boundColumn.Binding as Binding;
            return binding == null || binding.Path == null ? null : binding.Path.Path;
        }

        private static void AddLiveSortingProperty(ICollectionViewLiveShaping liveView, string propertyName)
        {
            if (!liveView.LiveSortingProperties.Contains(propertyName))
            {
                liveView.LiveSortingProperties.Add(propertyName);
            }
        }

        private async void RefreshProcesses(string resultText)
        {
            if (_isRefreshing)
            {
                if (!string.IsNullOrWhiteSpace(resultText))
                {
                    _pendingRefreshResultText = resultText;
                    _refreshRequested = true;
                }

                return;
            }

            _isRefreshing = true;
            try
            {
                do
                {
                    var currentResultText = string.IsNullOrWhiteSpace(_pendingRefreshResultText)
                        ? resultText
                        : _pendingRefreshResultText;
                    _pendingRefreshResultText = null;
                    _refreshRequested = false;

                    var targetProcessNames = LoadTargetProcessNames();
                    _targetProcessNames = targetProcessNames;
                    SyncFilterOptions(targetProcessNames);

                    var filterAtStart = _selectedProcessNameFilter;
                    var activeTargetProcessNames = new HashSet<string>(GetActiveTargetProcessNames(), StringComparer.OrdinalIgnoreCase);
                    var snapshot = await Task.Run(() => GetProcessSnapshot(activeTargetProcessNames));

                    if (!string.Equals(filterAtStart, _selectedProcessNameFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        _refreshRequested = true;
                        continue;
                    }

                    ApplyProcessSnapshot(snapshot);
                    UpdateStatus(currentResultText);
                    resultText = null;
                }
                while (_refreshRequested);
            }
            catch (Exception ex)
            {
                UpdateStatus("刷新失败：" + ex.Message);
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private List<ProcessInfo> GetProcessSnapshot(HashSet<string> activeTargetProcessNames)
        {
            var wmiProcessInfo = GetWmiProcessInfoById();
            var shellExplorerProcessId = GetShellExplorerProcessId();
            var processes = Process.GetProcesses();
            var snapshot = new List<ProcessInfo>();

            foreach (var process in processes)
            {
                try
                {
                    var processName = SafeGetProcessName(process);
                    var normalizedName = NormalizeProcessName(processName);
                    if (!activeTargetProcessNames.Contains(normalizedName))
                    {
                        continue;
                    }

                    ProcessWmiInfo wmiInfo;
                    wmiProcessInfo.TryGetValue(process.Id, out wmiInfo);
                    snapshot.Add(CreateProcessInfo(process, normalizedName, wmiInfo, shellExplorerProcessId));
                }
                finally
                {
                    process.Dispose();
                }
            }

            return SortProcessSnapshot(snapshot);
        }

        private static List<ProcessInfo> SortProcessSnapshot(IEnumerable<ProcessInfo> snapshot)
        {
            return snapshot
                .OrderBy(processInfo => processInfo.NormalizedName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(processInfo => processInfo.ExplorerKindSortOrder)
                .ThenBy(processInfo => processInfo.Id)
                .ToList();
        }

        private HashSet<string> GetActiveTargetProcessNames()
        {
            if (string.IsNullOrWhiteSpace(_selectedProcessNameFilter))
            {
                return _targetProcessNames;
            }

            return new HashSet<string>(new[] { _selectedProcessNameFilter }, StringComparer.OrdinalIgnoreCase);
        }

        private void SyncFilterOptions(HashSet<string> targetProcessNames)
        {
            if (!string.IsNullOrWhiteSpace(_selectedProcessNameFilter) &&
                !targetProcessNames.Contains(_selectedProcessNameFilter))
            {
                _selectedProcessNameFilter = AllProcessFilterName;
            }

            var desiredOptions = CreateFilterOptions(targetProcessNames);

            _isUpdatingFilterOptions = true;
            try
            {
                if (!FilterOptionsMatch(desiredOptions))
                {
                    _processFilterOptions.Clear();
                    foreach (var desiredOption in desiredOptions)
                    {
                        _processFilterOptions.Add(desiredOption);
                    }
                }

                var selectedOption = _processFilterOptions.FirstOrDefault(option =>
                    string.Equals(option.NormalizedName, _selectedProcessNameFilter, StringComparison.OrdinalIgnoreCase));
                ProcessFilterComboBox.SelectedItem = selectedOption ?? _processFilterOptions.FirstOrDefault();
            }
            finally
            {
                _isUpdatingFilterOptions = false;
            }
        }

        private static List<ProcessFilterOption> CreateFilterOptions(IEnumerable<string> targetProcessNames)
        {
            var options = new List<ProcessFilterOption>
            {
                new ProcessFilterOption
                {
                    DisplayName = AllProcessFilterDisplayName,
                    NormalizedName = AllProcessFilterName
                }
            };

            options.AddRange(targetProcessNames
                .OrderBy(processName => processName, StringComparer.OrdinalIgnoreCase)
                .Select(processName => new ProcessFilterOption
                {
                    DisplayName = ToExecutableDisplayName(processName),
                    NormalizedName = processName
                }));

            return options;
        }

        private bool FilterOptionsMatch(IList<ProcessFilterOption> desiredOptions)
        {
            if (_processFilterOptions.Count != desiredOptions.Count)
            {
                return false;
            }

            for (var index = 0; index < desiredOptions.Count; index++)
            {
                if (!string.Equals(_processFilterOptions[index].NormalizedName, desiredOptions[index].NormalizedName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_processFilterOptions[index].DisplayName, desiredOptions[index].DisplayName, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private void ApplyProcessSnapshot(List<ProcessInfo> snapshot)
        {
            var selectedIds = new HashSet<int>(ProcessGrid.SelectedItems.Cast<ProcessInfo>().Select(processInfo => processInfo.Id));
            var snapshotById = snapshot.ToDictionary(processInfo => processInfo.Id);

            for (var index = _processes.Count - 1; index >= 0; index--)
            {
                if (!snapshotById.ContainsKey(_processes[index].Id))
                {
                    _processes.RemoveAt(index);
                }
            }

            var existingById = _processes.ToDictionary(processInfo => processInfo.Id);
            for (var desiredIndex = 0; desiredIndex < snapshot.Count; desiredIndex++)
            {
                var newInfo = snapshot[desiredIndex];
                ProcessInfo existingInfo;
                if (existingById.TryGetValue(newInfo.Id, out existingInfo))
                {
                    existingInfo.UpdateFrom(newInfo);

                    var currentIndex = _processes.IndexOf(existingInfo);
                    if (currentIndex >= 0 && currentIndex != desiredIndex)
                    {
                        _processes.Move(currentIndex, desiredIndex);
                    }
                }
                else
                {
                    _processes.Insert(Math.Min(desiredIndex, _processes.Count), newInfo);
                }
            }

            RestoreSelection(selectedIds);
        }

        private void RestoreSelection(HashSet<int> selectedIds)
        {
            if (selectedIds.Count == 0)
            {
                return;
            }

            var remainingIds = new HashSet<int>(_processes.Select(processInfo => processInfo.Id));
            selectedIds.IntersectWith(remainingIds);
            var currentSelectedIds = new HashSet<int>(ProcessGrid.SelectedItems.Cast<ProcessInfo>().Select(processInfo => processInfo.Id));
            if (currentSelectedIds.SetEquals(selectedIds))
            {
                return;
            }

            ProcessGrid.SelectedItems.Clear();
            foreach (var processInfo in _processes.Where(processInfo => selectedIds.Contains(processInfo.Id)))
            {
                ProcessGrid.SelectedItems.Add(processInfo);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= RefreshTimer_Tick;
            base.OnClosed(e);
        }

        private ProcessInfo CreateProcessInfo(Process process, string normalizedName, ProcessWmiInfo wmiInfo, int? shellExplorerProcessId)
        {
            var memoryBytes = SafeReadLong(() => process.WorkingSet64, -1);
            var threadCount = SafeReadInt(() => process.Threads.Count, -1);
            var commandLine = wmiInfo == null ? null : wmiInfo.CommandLine;
            var explorerKind = GetExplorerProcessKind(normalizedName, process.Id, shellExplorerProcessId, commandLine);

            return new ProcessInfo
            {
                Id = process.Id,
                Name = ToExecutableDisplayName(normalizedName),
                NormalizedName = normalizedName,
                ExplorerKind = explorerKind,
                RowForeground = GetProcessTypeColor(normalizedName, explorerKind),
                SessionId = SafeRead(() => process.SessionId.ToString(CultureInfo.InvariantCulture)),
                MemoryBytes = memoryBytes,
                MemoryDisplay = memoryBytes < 0 ? MissingText : FormatMemory(memoryBytes),
                ThreadCountValue = threadCount,
                ThreadCount = threadCount < 0 ? MissingText : threadCount.ToString(CultureInfo.InvariantCulture),
                StartTimeDisplay = SafeRead(() => process.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                MainWindowTitle = EmptyToDash(SafeRead(() => process.MainWindowTitle)),
                ProcessPath = EmptyToMissing(wmiInfo == null ? null : wmiInfo.ExecutablePath),
                CommandLine = EmptyToMissing(commandLine)
            };
        }

        private void KillProcesses(IEnumerable<ProcessInfo> processInfos)
        {
            var requestedProcesses = processInfos
                .Where(processInfo => processInfo != null)
                .GroupBy(processInfo => processInfo.Id)
                .Select(group => group.First())
                .ToList();

            if (requestedProcesses.Count == 0)
            {
                UpdateStatus("没有可结束的进程。");
                return;
            }

            var processesToKill = requestedProcesses
                .Where(processInfo => processInfo.Id != _currentProcessId)
                .ToList();
            var skippedCurrentProcessCount = requestedProcesses.Count - processesToKill.Count;

            if (processesToKill.Count == 0)
            {
                UpdateStatus("已跳过当前程序，未结束任何进程。");
                return;
            }

            if (processesToKill.Any(processInfo => processInfo.IsExplorer))
            {
                var result = MessageBox.Show(
                    this,
                    "将结束 explorer.exe，桌面和任务栏可能会关闭。\n\n确定继续吗？",
                    "确认结束 explorer.exe",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    UpdateStatus("已取消结束操作。");
                    return;
                }
            }

            var successCount = 0;
            var skippedCount = skippedCurrentProcessCount;
            var failures = new List<string>();

            foreach (var processInfo in processesToKill)
            {
                try
                {
                    using (var process = Process.GetProcessById(processInfo.Id))
                    {
                        var currentName = NormalizeProcessName(SafeGetProcessName(process));
                        if (!string.Equals(currentName, processInfo.NormalizedName, StringComparison.OrdinalIgnoreCase))
                        {
                            skippedCount++;
                            continue;
                        }

                        if (process.HasExited)
                        {
                            skippedCount++;
                            continue;
                        }

                        process.Kill();
                        process.WaitForExit(1000);
                        successCount++;
                    }
                }
                catch (ArgumentException)
                {
                    skippedCount++;
                }
                catch (InvalidOperationException)
                {
                    skippedCount++;
                }
                catch (Exception ex)
                {
                    failures.Add(string.Format(
                        CultureInfo.CurrentCulture,
                        "{0}({1}): {2}",
                        processInfo.Name,
                        processInfo.Id,
                        ex.Message));
                }
            }

            var resultText = BuildKillResultText(requestedProcesses.Count, successCount, skippedCount, failures.Count);
            if (failures.Count > 0)
            {
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine, failures.Take(8)),
                    "部分进程结束失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            RefreshProcesses(resultText);
        }

        private static HashSet<string> LoadTargetProcessNames()
        {
            string rawValue = null;
            try
            {
                ConfigurationManager.RefreshSection("appSettings");
                rawValue = ConfigurationManager.AppSettings[TargetProcessNamesSetting];
            }
            catch (ConfigurationErrorsException)
            {
                rawValue = null;
            }

            var names = ParseTargetProcessNames(rawValue);
            if (names.Count == 0)
            {
                names = ParseTargetProcessNames(DefaultTargetProcessNames);
            }

            return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }

        private static List<string> ParseTargetProcessNames(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return new List<string>();
            }

            return rawValue
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeProcessName)
                .Where(processName => !string.IsNullOrWhiteSpace(processName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static Dictionary<int, ProcessWmiInfo> GetWmiProcessInfoById()
        {
            var processInfoById = new Dictionary<int, ProcessWmiInfo>();

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process"))
                using (var processObjects = searcher.Get())
                {
                    foreach (ManagementObject processObject in processObjects)
                    {
                        using (processObject)
                        {
                            var processIdValue = processObject["ProcessId"];
                            if (processIdValue == null)
                            {
                                continue;
                            }

                            var processId = Convert.ToInt32(processIdValue, CultureInfo.InvariantCulture);
                            processInfoById[processId] = new ProcessWmiInfo
                            {
                                ExecutablePath = Convert.ToString(processObject["ExecutablePath"], CultureInfo.CurrentCulture),
                                CommandLine = Convert.ToString(processObject["CommandLine"], CultureInfo.CurrentCulture)
                            };
                        }
                    }
                }
            }
            catch (ManagementException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (SystemException)
            {
            }

            return processInfoById;
        }

        private void UpdateStatus(string resultText)
        {
            if (!string.IsNullOrWhiteSpace(resultText))
            {
                _lastResult = resultText;
            }

            ProcessCountTextBlock.Text = string.Format(
                CultureInfo.CurrentCulture,
                "当前列表：{0} 个；已选：{1} 个",
                _processes.Count,
                ProcessGrid == null ? 0 : ProcessGrid.SelectedItems.Count);
            LastResultTextBlock.Text = _lastResult;
            KillSelectedButton.IsEnabled = ProcessGrid != null && ProcessGrid.SelectedItems.Count > 0;
        }

        private static string BuildKillResultText(int requestedCount, int successCount, int skippedCount, int failureCount)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "结束完成：请求 {0} 个，成功 {1} 个，跳过 {2} 个，失败 {3} 个。",
                requestedCount,
                successCount,
                skippedCount,
                failureCount);
        }

        private static string NormalizeProcessName(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return string.Empty;
            }

            var normalized = processName.Trim();
            if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(0, normalized.Length - 4);
            }

            return normalized;
        }

        private static string ToExecutableDisplayName(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return MissingText;
            }

            return processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName
                : processName + ".exe";
        }

        private static int? GetShellExplorerProcessId()
        {
            try
            {
                var shellWindow = NativeMethods.GetShellWindow();
                if (shellWindow == IntPtr.Zero)
                {
                    return null;
                }

                uint processId;
                NativeMethods.GetWindowThreadProcessId(shellWindow, out processId);
                if (processId == 0 || processId > int.MaxValue)
                {
                    return null;
                }

                return (int)processId;
            }
            catch (SystemException)
            {
                return null;
            }
        }

        private static ExplorerProcessKind GetExplorerProcessKind(string normalizedName, int processId, int? shellExplorerProcessId, string commandLine)
        {
            if (!string.Equals(normalizedName, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                return ExplorerProcessKind.None;
            }

            if (shellExplorerProcessId.HasValue)
            {
                return processId == shellExplorerProcessId.Value
                    ? ExplorerProcessKind.Shell
                    : ExplorerProcessKind.Additional;
            }

            if (!string.IsNullOrWhiteSpace(commandLine) &&
                (commandLine.IndexOf("/factory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 commandLine.IndexOf("-Embedding", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return ExplorerProcessKind.Additional;
            }

            return ExplorerProcessKind.Unknown;
        }

        private static int GetExplorerKindSortOrder(ExplorerProcessKind explorerKind)
        {
            if (explorerKind == ExplorerProcessKind.Additional)
            {
                return 1;
            }

            if (explorerKind == ExplorerProcessKind.Unknown)
            {
                return 2;
            }

            return 0;
        }

        private static string GetProcessTypeColor(string normalizedName, ExplorerProcessKind explorerKind)
        {
            if (string.Equals(normalizedName, "java", StringComparison.OrdinalIgnoreCase))
            {
                return "#0B7A32";
            }

            if (string.Equals(normalizedName, "javaw", StringComparison.OrdinalIgnoreCase))
            {
                return "#1D4ED8";
            }

            if (string.Equals(normalizedName, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                if (explorerKind == ExplorerProcessKind.Shell)
                {
                    return "#B45309";
                }

                if (explorerKind == ExplorerProcessKind.Additional)
                {
                    return "#0891B2";
                }

                return "#6B7280";
            }

            var index = GetStableColorIndex(normalizedName, FallbackProcessColors.Length);
            return FallbackProcessColors[index];
        }

        private static int GetStableColorIndex(string value, int colorCount)
        {
            if (colorCount <= 0)
            {
                return 0;
            }

            unchecked
            {
                var hash = 2166136261U;
                var safeValue = value ?? string.Empty;
                foreach (var character in safeValue.ToUpperInvariant())
                {
                    hash ^= character;
                    hash *= 16777619;
                }

                return (int)(hash % (uint)colorCount);
            }
        }

        private static string SafeGetProcessName(Process process)
        {
            try
            {
                return process.ProcessName;
            }
            catch (InvalidOperationException)
            {
                return string.Empty;
            }
        }

        private static string SafeRead(Func<string> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return MissingText;
            }
        }

        private static int SafeReadInt(Func<int> read, int fallbackValue)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return fallbackValue;
            }
        }

        private static long SafeReadLong(Func<long> read, long fallbackValue)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return fallbackValue;
            }
        }

        private static string FormatMemory(long bytes)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:N1} MB", bytes / 1024d / 1024d);
        }

        private static string EmptyToDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private static string EmptyToMissing(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? MissingText : value;
        }

        private static T FindVisualParent<T>(DependencyObject dependencyObject)
            where T : DependencyObject
        {
            while (dependencyObject != null)
            {
                var typedObject = dependencyObject as T;
                if (typedObject != null)
                {
                    return typedObject;
                }

                try
                {
                    dependencyObject = VisualTreeHelper.GetParent(dependencyObject);
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }

            return null;
        }

        public class ProcessFilterOption
        {
            public string DisplayName { get; set; }
            public string NormalizedName { get; set; }
        }

        public class ProcessInfo : INotifyPropertyChanged
        {
            private int _id;
            private string _name;
            private string _normalizedName;
            private string _sessionId;
            private long _memoryBytes;
            private string _memoryDisplay;
            private int _threadCountValue;
            private string _threadCount;
            private string _startTimeDisplay;
            private string _mainWindowTitle;
            private string _processPath;
            private string _commandLine;
            private string _rowForeground;
            private ExplorerProcessKind _explorerKind;

            public event PropertyChangedEventHandler PropertyChanged;

            public int Id
            {
                get { return _id; }
                set { SetProperty(ref _id, value, "Id"); }
            }

            public string Name
            {
                get { return _name; }
                set { SetProperty(ref _name, value, "Name"); }
            }

            public string NormalizedName
            {
                get { return _normalizedName; }
                set
                {
                    if (SetProperty(ref _normalizedName, value, "NormalizedName"))
                    {
                        OnPropertyChanged("IsExplorer");
                    }
                }
            }

            public string SessionId
            {
                get { return _sessionId; }
                set { SetProperty(ref _sessionId, value, "SessionId"); }
            }

            public string MemoryDisplay
            {
                get { return _memoryDisplay; }
                set { SetProperty(ref _memoryDisplay, value, "MemoryDisplay"); }
            }

            public long MemoryBytes
            {
                get { return _memoryBytes; }
                set { SetProperty(ref _memoryBytes, value, "MemoryBytes"); }
            }

            public string ThreadCount
            {
                get { return _threadCount; }
                set { SetProperty(ref _threadCount, value, "ThreadCount"); }
            }

            public int ThreadCountValue
            {
                get { return _threadCountValue; }
                set { SetProperty(ref _threadCountValue, value, "ThreadCountValue"); }
            }

            public string StartTimeDisplay
            {
                get { return _startTimeDisplay; }
                set { SetProperty(ref _startTimeDisplay, value, "StartTimeDisplay"); }
            }

            public string MainWindowTitle
            {
                get { return _mainWindowTitle; }
                set { SetProperty(ref _mainWindowTitle, value, "MainWindowTitle"); }
            }

            public string ProcessPath
            {
                get { return _processPath; }
                set { SetProperty(ref _processPath, value, "ProcessPath"); }
            }

            public string CommandLine
            {
                get { return _commandLine; }
                set { SetProperty(ref _commandLine, value, "CommandLine"); }
            }

            public string RowForeground
            {
                get { return _rowForeground; }
                set { SetProperty(ref _rowForeground, value, "RowForeground"); }
            }

            internal ExplorerProcessKind ExplorerKind
            {
                get { return _explorerKind; }
                set
                {
                    if (SetProperty(ref _explorerKind, value, "ExplorerKind"))
                    {
                        OnPropertyChanged("ExplorerKindSortOrder");
                    }
                }
            }

            public int ExplorerKindSortOrder
            {
                get { return GetExplorerKindSortOrder(ExplorerKind); }
            }

            public bool IsExplorer
            {
                get { return string.Equals(NormalizedName, "explorer", StringComparison.OrdinalIgnoreCase); }
            }

            public void UpdateFrom(ProcessInfo processInfo)
            {
                Id = processInfo.Id;
                Name = processInfo.Name;
                NormalizedName = processInfo.NormalizedName;
                SessionId = processInfo.SessionId;
                MemoryBytes = processInfo.MemoryBytes;
                MemoryDisplay = processInfo.MemoryDisplay;
                ThreadCountValue = processInfo.ThreadCountValue;
                ThreadCount = processInfo.ThreadCount;
                StartTimeDisplay = processInfo.StartTimeDisplay;
                MainWindowTitle = processInfo.MainWindowTitle;
                ProcessPath = processInfo.ProcessPath;
                CommandLine = processInfo.CommandLine;
                RowForeground = processInfo.RowForeground;
                ExplorerKind = processInfo.ExplorerKind;
            }

            private bool SetProperty<T>(ref T field, T value, string propertyName)
            {
                if (EqualityComparer<T>.Default.Equals(field, value))
                {
                    return false;
                }

                field = value;
                OnPropertyChanged(propertyName);
                return true;
            }

            private void OnPropertyChanged(string propertyName)
            {
                var handler = PropertyChanged;
                if (handler != null)
                {
                    handler(this, new PropertyChangedEventArgs(propertyName));
                }
            }
        }

        private class ProcessWmiInfo
        {
            public string ExecutablePath { get; set; }
            public string CommandLine { get; set; }
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll")]
            internal static extern IntPtr GetShellWindow();

            [DllImport("user32.dll")]
            internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        }
    }
}
