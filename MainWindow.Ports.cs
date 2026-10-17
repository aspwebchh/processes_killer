using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace ProcessesKiller
{
    public partial class MainWindow
    {
        private readonly PortQueryService _portQueryService = new PortQueryService();
        private readonly ObservableCollection<PortProcessRow> _portProcesses = new ObservableCollection<PortProcessRow>();
        private int? _queriedPort;
        private bool _hasPortQuery;
        private PortProtocolFilter _queriedProtocol;
        private long _portVersion;
        private bool _portConditionsDirty;
        private bool _isQueryingPort;
        private bool _portRefreshRequested;
        private bool _portReportNextQuery;
        private bool _portQueryComplete;
        private bool _isConfirmingPortKill;
        private string _pendingPortOperationText;
        private string _portLastResult = "尚未查询";
        private string _portState = "尚未查询";
        private string _portOperationDetails;

        public ObservableCollection<PortProcessRow> PortProcesses { get { return _portProcesses; } }
        private bool IsProcessTabActive { get { return WorkspaceTabs == null || WorkspaceTabs.SelectedIndex == 0; } }

        private void EnablePortSorting()
        {
            var view = CollectionViewSource.GetDefaultView(_portProcesses);
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                view.SortDescriptions.Add(new SortDescription("Id", ListSortDirection.Ascending));
                view.SortDescriptions.Add(new SortDescription("Port", ListSortDirection.Ascending));
            }
        }

        private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady || e.Source != WorkspaceTabs) return;
            _viewRevision++;
            _portVersion++;
            if (IsProcessTabActive) RefreshProcesses(null);
            else RefreshPortResults(false);
        }

        private void PortConditions_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isUiReady) return;
            _portVersion++;
            _portConditionsDirty = _hasPortQuery;
            _portQueryComplete = false;
            PortInputErrorTextBlock.Text = string.Empty;
            _portState = _hasPortQuery ? "条件已修改，请查询" : "尚未查询";
            UpdatePortStatus();
        }

        private void PortInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; SubmitPortQuery(); }
        }

        private void PortQueryButton_Click(object sender, RoutedEventArgs e) { SubmitPortQuery(); }

        private void SubmitPortQuery()
        {
            if (_isTerminating || _isConfirmingPortKill || _isClosing) return;
            int? port = null;
            int parsedPort;
            if (!string.IsNullOrWhiteSpace(PortInputTextBox.Text))
            {
                if (!PortQueryService.TryParsePort(PortInputTextBox.Text, out parsedPort))
                {
                    PortInputErrorTextBlock.Text = "请输入 1–65535 的整数，或留空查询全部。";
                    PortInputTextBox.Focus();
                    return;
                }
                port = parsedPort;
            }
            PortInputErrorTextBlock.Text = string.Empty;
            var protocol = (PortProtocolFilter)PortProtocolComboBox.SelectedIndex;
            var changed = !_hasPortQuery || _queriedPort != port || _queriedProtocol != protocol;
            _queriedPort = port;
            _hasPortQuery = true;
            _queriedProtocol = protocol;
            _portConditionsDirty = false;
            _portQueryComplete = false;
            _portVersion++;
            if (changed) _portProcesses.Clear();
            RefreshPortResults(true);
        }

        private async void RefreshPortResults(bool manual)
        {
            await RefreshPortResultsAsync(manual, null);
        }

        private async Task RefreshPortResultsAsync(bool manual, string operationText)
        {
            if (_isClosing || IsProcessTabActive || _isTerminating || _isConfirmingPortKill || _portConditionsDirty || !_hasPortQuery) return;
            if (operationText != null) _pendingPortOperationText = operationText;
            _portReportNextQuery |= manual;
            if (_isQueryingPort)
            {
                _portRefreshRequested |= manual;
                return;
            }
            _isQueryingPort = true;
            var version = _portVersion;
            try
            {
                do
                {
                    _portRefreshRequested = false;
                    version = _portVersion;
                    var port = _queriedPort;
                    var protocol = _queriedProtocol;
                    _portState = "正在查询" + (port.HasValue ? "端口 " + port.Value : "全部端口") + "…";
                    UpdatePortStatus();
                    var snapshot = await Task.Run(() => GetPortSnapshot(port, protocol));
                    if (_isClosing) return;
                    if (version != _portVersion || IsProcessTabActive || _portConditionsDirty || _isTerminating || _isConfirmingPortKill)
                    {
                        _portRefreshRequested = !IsProcessTabActive && !_portConditionsDirty && !_isTerminating
                            && !_isConfirmingPortKill && _hasPortQuery;
                        continue;
                    }
                    ApplyPortSnapshot(snapshot.Rows);
                    _portQueryComplete = snapshot.Query.IsComplete;
                    var queryLabel = QueryLabel(port, protocol);
                    if (!snapshot.Query.IsComplete)
                    {
                        _portState = (snapshot.Query.SuccessfulTables == 0 ? "查询失败" : "查询不完整")
                            + "，无法完整确认端口状态。" + Environment.NewLine + string.Join(Environment.NewLine, snapshot.Query.Errors);
                    }
                    else if (snapshot.Rows.Count == 0) _portState = queryLabel + "：当前已无监听/绑定。";
                    else
                    {
                        _portState = queryLabel;
                        if (snapshot.HasUnavailableDetails) _portState += " 部分进程详情无权限/不可用。";
                    }
                    if (_pendingPortOperationText != null)
                    {
                        _portLastResult = _pendingPortOperationText;
                        _pendingPortOperationText = null;
                    }
                    else if (_portReportNextQuery)
                    {
                        _portLastResult = snapshot.Query.IsComplete ? "查询完成" : "查询未完整完成";
                    }
                    _portReportNextQuery = false;
                }
                while (_portRefreshRequested);
            }
            catch (Exception ex)
            {
                if (!_isClosing && version == _portVersion && !IsProcessTabActive && !_portConditionsDirty && !_isTerminating && !_isConfirmingPortKill)
                {
                    _portQueryComplete = false;
                    _portState = "查询失败，无法完整确认端口状态：" + ex.Message;
                    _portLastResult = _pendingPortOperationText ?? "查询失败";
                    _pendingPortOperationText = null;
                }
            }
            finally
            {
                _isQueryingPort = false;
                if (!_isClosing) UpdatePortStatus();
            }
        }

        private PortSnapshot GetPortSnapshot(int? port, PortProtocolFilter protocol)
        {
            var snapshot = new PortSnapshot { Query = _portQueryService.Query(port, protocol) };
            if (snapshot.Query.Endpoints.Count == 0) return snapshot;
            var wmiInfoById = GetWmiProcessInfoById();
            var shellId = GetShellExplorerProcessId();
            var infoById = new Dictionary<int, ProcessInfo>();
            foreach (var pid in snapshot.Query.Endpoints.Select(endpoint => endpoint.ProcessId).Distinct())
            {
                ProcessInfo info;
                try
                {
                    using (var process = Process.GetProcessById(pid))
                    {
                        ProcessWmiInfo wmiInfo;
                        wmiInfoById.TryGetValue(pid, out wmiInfo);
                        info = CreateProcessInfo(process, NormalizeProcessName(SafeGetProcessName(process)), wmiInfo, shellId);
                        if (string.IsNullOrWhiteSpace(info.NormalizedName)) info.Name = MissingText;
                    }
                }
                catch (Exception)
                {
                    info = new ProcessInfo { Id = pid, Name = "已退出/不可用", NormalizedName = string.Empty,
                        StartTimeDisplay = MissingText, ProcessPath = MissingText, CommandLine = MissingText, RowForeground = "#9CA3AF" };
                }
                infoById.Add(pid, info);
                if (!info.StartTimeUtcTicks.HasValue || info.ProcessPath == MissingText || info.CommandLine == MissingText)
                    snapshot.HasUnavailableDetails = true;
            }
            foreach (var group in snapshot.Query.Endpoints
                .GroupBy(endpoint => new { endpoint.ProcessId, endpoint.Port, endpoint.Protocol })
                .OrderBy(group => group.Key.Protocol)
                .ThenBy(group => group.Key.Port)
                .ThenBy(group => group.Key.ProcessId))
            {
                var endpoints = group
                    .OrderBy(endpoint => endpoint.IpVersion == "IPv6" ? 1 : 0)
                    .ThenBy(endpoint => endpoint.LocalAddress, StringComparer.Ordinal)
                    .ToList();
                snapshot.Rows.Add(new PortProcessRow(endpoints, infoById[group.Key.ProcessId], _currentProcessId));
            }
            return snapshot;
        }

        private void ApplyPortSnapshot(List<PortProcessRow> rows)
        {
            var selectedKeys = new HashSet<string>(PortProcessGrid.SelectedItems.Cast<PortProcessRow>().Select(row => row.IdentityKey));
            var desired = rows.ToDictionary(row => row.IdentityKey);
            for (var index = _portProcesses.Count - 1; index >= 0; index--)
                if (!desired.ContainsKey(_portProcesses[index].IdentityKey)) _portProcesses.RemoveAt(index);
            var existing = _portProcesses.ToDictionary(row => row.IdentityKey);
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                PortProcessRow oldRow;
                if (existing.TryGetValue(row.IdentityKey, out oldRow))
                {
                    oldRow.UpdateFrom(row);
                    var oldIndex = _portProcesses.IndexOf(oldRow);
                    if (oldIndex != index) _portProcesses.Move(oldIndex, index);
                }
                else _portProcesses.Insert(index, row);
            }
            var currentKeys = new HashSet<string>(PortProcessGrid.SelectedItems.Cast<PortProcessRow>().Select(row => row.IdentityKey));
            selectedKeys.IntersectWith(desired.Keys);
            if (!currentKeys.SetEquals(selectedKeys))
            {
                PortProcessGrid.SelectedItems.Clear();
                foreach (var row in _portProcesses.Where(row => selectedKeys.Contains(row.IdentityKey))) PortProcessGrid.SelectedItems.Add(row);
            }
        }

        private async void KillPortSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isClosing || _isTerminating || _isConfirmingPortKill || _isQueryingPort || !_portQueryComplete || _portConditionsDirty || !_hasPortQuery) return;
            var selectedRows = PortProcessGrid.SelectedItems.Cast<PortProcessRow>().ToList();
            var selectedKeysById = selectedRows.GroupBy(row => row.Id).ToDictionary(group => group.Key,
                group => new HashSet<string>(group.SelectMany(row => row.Endpoints.Select(endpoint => endpoint.Key))));
            var targets = selectedRows.Select(row => CreateTarget(row.Process, true))
                .GroupBy(target => target.Id).Select(group => group.First()).ToList();
            if (targets.Count == 0) return;
            var port = _queriedPort;
            var protocol = _queriedProtocol;
            _isConfirmingPortKill = true;
            _portVersion++;
            _viewRevision++;
            UpdateOperationButtons();
            try
            {
                var text = QueryLabel(port, protocol) + "\n将结束整个进程，同时影响它的其他端口；不可操作的目标会跳过。";
                var targetList = string.Join(Environment.NewLine + Environment.NewLine, selectedRows.GroupBy(row => row.Id).Select(group =>
                    group.First().Name + " (PID " + group.Key + ")" + Environment.NewLine
                    + string.Join(Environment.NewLine, group.OrderBy(row => row.Port).ThenBy(row => row.Protocol).Select(row => "  " + FormatEndpoint(row)))));
                if (targets.Any(target => target.NormalizedName.Equals("explorer", StringComparison.OrdinalIgnoreCase)))
                    text += "\n包含 explorer.exe，桌面和任务栏可能会关闭。";
                var confirmed = TerminationConfirmation != null
                    ? TerminationConfirmation(text + Environment.NewLine + targetList, "确认结束端口进程")
                    : ProcessDialogs.Create(this, "确认结束端口进程", text, targetList, true).ShowDialog() == true;
                if (!confirmed)
                {
                    _portLastResult = "已取消结束操作";
                    return;
                }
                _isTerminating = true;
                _portState = "正在结束进程，完成后重新检查端口…";
                UpdatePortStatus();
                var result = await Task.Run(() => ProcessTerminationService.Terminate(targets, _currentProcessId,
                    _windowLifetime.Token, target =>
                        _portQueryService.ValidateOwnership(port, protocol, target.Id, selectedKeysById[target.Id])));
                if (_isClosing) return;
                _portLastResult = result.Summary;
                _portOperationDetails = result.Summary + Environment.NewLine
                    + string.Join(Environment.NewLine, result.Notes.Concat(result.Failures));
            }
            catch (Exception ex)
            {
                if (!_isClosing) _portLastResult = "结束失败：" + ex.Message;
            }
            finally
            {
                _isTerminating = false;
                _isConfirmingPortKill = false;
                if (!_isClosing)
                {
                    UpdateOperationButtons();
                    if (!IsProcessTabActive) await RefreshPortResultsAsync(false, _portLastResult);
                    else RefreshProcesses(null);
                }
            }
        }

        private void PortGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isUiReady) return;
            UpdatePortStatus();
        }

        private void PortGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row != null) CreatePortDetailsDialog((PortProcessRow)row.Item).ShowDialog();
        }

        private void ViewPortDetails_Click(object sender, RoutedEventArgs e)
        {
            var row = PortProcessGrid.SelectedItem as PortProcessRow;
            if (row != null) CreatePortDetailsDialog(row).ShowDialog();
        }

        private Window CreatePortDetailsDialog(PortProcessRow row)
        {
            return ProcessDialogs.Create(this, "进程详情", row.Name + " (PID " + row.Id + ")",
                FormatEndpoint(row) + " · " + row.IpVersion + " · " + row.State + "\n" + row.OperationHint
                + "\n\n启动时间\n" + row.Process.StartTimeDisplay + "\n\n可执行路径\n" + row.Process.ProcessPath
                + "\n\n命令行\n" + row.Process.CommandLine, false);
        }

        private static string FormatEndpoint(PortProcessRow row)
        {
            return row.Protocol + " " + string.Join(", ", row.Endpoints.Select(endpoint =>
                (endpoint.IpVersion == "IPv6" ? "[" + endpoint.LocalAddress + "]" : endpoint.LocalAddress) + ":" + endpoint.Port));
        }

        private void PortGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row == null) { e.Handled = true; return; }
            if (!row.IsSelected) { PortProcessGrid.SelectedItems.Clear(); row.IsSelected = true; }
            PortProcessGrid.CurrentItem = row.Item;
        }

        private void PortOperationDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_portOperationDetails))
                ProcessDialogs.Create(this, "端口进程操作详情", "最近一次结束操作", _portOperationDetails, false).ShowDialog();
        }

        private void UpdatePortStatus()
        {
            if (!_isUiReady || _isClosing) return;
            PortCountTextBlock.Text = string.Format("{0} 个进程 · {1} 项 · 已选 {2} 项", _portProcesses.Select(row => row.Id).Distinct().Count(),
                _portProcesses.Count, PortProcessGrid.SelectedItems.Count);
            var showState = _isQueryingPort || _isTerminating || _portConditionsDirty || !_portQueryComplete || _portLastResult == "查询完成";
            PortLastResultTextBlock.Text = showState ? _portState : _portLastResult;
            PortResultDetailsButton.IsEnabled = !showState && _portOperationDetails != null && _portOperationDetails.StartsWith(_portLastResult, StringComparison.Ordinal);
            PortResultDetailsButton.ToolTip = PortResultDetailsButton.IsEnabled ? "点击查看结束详情" : _portState;
            UpdateOperationButtons();
        }

        private void UpdateOperationButtons()
        {
            if (!_isUiReady || _isClosing) return;
            var busy = _isTerminating || _isConfirmingPortKill;
            KillSelectedButton.IsEnabled = !busy && ProcessGrid.SelectedItems.Count > 0;
            KillAllButton.IsEnabled = !busy && _processes.Count > 0;
            RefreshProcessButton.IsEnabled = !busy;
            ProcessFilterComboBox.IsEnabled = !busy;
            ProcessKillMenuItem.IsEnabled = !busy && ProcessGrid.SelectedItems.Count > 0;
            PortInputTextBox.IsEnabled = !busy;
            PortProtocolComboBox.IsEnabled = !busy;
            PortQueryButton.IsEnabled = !busy;
            var canKill = !busy && !_isQueryingPort && !_portConditionsDirty && _portQueryComplete
                && PortProcessGrid.SelectedItems.Cast<PortProcessRow>().Any(row => row.CanTerminate);
            KillPortSelectedButton.IsEnabled = canKill;
            PortKillMenuItem.IsEnabled = canKill;
            PortViewDetailsMenuItem.IsEnabled = PortProcessGrid.SelectedItem != null;
        }

        private static ProcessTarget CreateTarget(ProcessInfo info, bool requireIdentity)
        {
            return new ProcessTarget { Id = info.Id, Name = info.Name, NormalizedName = info.NormalizedName ?? string.Empty,
                StartTimeUtcTicks = info.StartTimeUtcTicks, RequireIdentity = requireIdentity };
        }

        private static long? ReadStartTimeUtcTicks(Process process)
        {
            try { return process.StartTime.ToUniversalTime().Ticks; }
            catch (Exception) { return null; }
        }

        private static string ProtocolLabel(PortProtocolFilter protocol)
        {
            return protocol == PortProtocolFilter.All ? "TCP + UDP" : protocol == PortProtocolFilter.Tcp ? "TCP" : "UDP";
        }

        private static string QueryLabel(int? port, PortProtocolFilter protocol)
        {
            return (port.HasValue ? "端口 " + port.Value : "全部端口") + " / " + ProtocolLabel(protocol);
        }

        private sealed class PortSnapshot
        {
            public PortQueryResult Query;
            public readonly List<PortProcessRow> Rows = new List<PortProcessRow>();
            public bool HasUnavailableDetails;
        }

        public sealed class PortProcessRow : INotifyPropertyChanged
        {
            internal IReadOnlyList<PortEndpoint> Endpoints { get; private set; }
            public ProcessInfo Process { get; private set; }
            private readonly int _currentProcessId;
            internal PortProcessRow(IReadOnlyList<PortEndpoint> endpoints, ProcessInfo process, int currentProcessId)
            { Endpoints = endpoints; Process = process; _currentProcessId = currentProcessId; }
            public event PropertyChangedEventHandler PropertyChanged;
            public string Protocol { get { return Endpoints[0].Protocol; } }
            public string IpVersion { get { return string.Join(" + ", Endpoints.Select(endpoint => endpoint.IpVersion).Distinct()); } }
            public string LocalAddress { get { return string.Join(", ", Endpoints.Select(endpoint => endpoint.LocalAddress).Distinct()); } }
            public int Port { get { return Endpoints[0].Port; } }
            public string State { get { return Endpoints[0].State; } }
            public int Id { get { return Endpoints[0].ProcessId; } }
            public string Name { get { return Process.Name; } }
            public string RowForeground { get { return Process.RowForeground; } }
            public bool CanTerminate { get { return Id > 4 && Id != _currentProcessId && Process.StartTimeUtcTicks.HasValue && !string.IsNullOrEmpty(Process.NormalizedName); } }
            public string OperationHint
            {
                get { return Id <= 4 ? "系统进程，不可结束" : Id == _currentProcessId ? "当前工具，不可结束" : CanTerminate ? "可结束" : "无法验证身份，不可结束"; }
            }
            internal string IdentityKey
            {
                get { return Protocol + "|" + Port.ToString(CultureInfo.InvariantCulture) + "|" + Id.ToString(CultureInfo.InvariantCulture) + "|" + Process.StartTimeUtcTicks + "|" + Process.NormalizedName; }
            }
            internal void UpdateFrom(PortProcessRow row)
            {
                Endpoints = row.Endpoints; Process = row.Process;
                var changed = PropertyChanged;
                if (changed != null) changed(this, new PropertyChangedEventArgs(string.Empty));
            }
        }
    }
}
