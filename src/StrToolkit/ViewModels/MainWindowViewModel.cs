using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using StrToolkit.Services;
using StrToolkit.Solvers;

namespace StrToolkit.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _solverCheckGate = new(1, 1);
    private CancellationTokenSource? _autoSelectCancellation;
    private int _autoSelectGeneration;
    private SolverItemViewModel? _hoveredSolver;
    private bool _isSolverAreaHovered;

    public ObservableCollection<SolverItemViewModel> Solvers { get; } = new();

    /// <summary>
    /// 工具栏只绑定可见项，避免隐藏项仍占位导致 StackPanel 间距不均。
    /// </summary>
    public ObservableCollection<SolverItemViewModel> VisibleSolvers { get; } = new();

    [ObservableProperty]
    private string _bodyText = "";

    /// <summary>是否处于"修改快捷键"模式。</summary>
    [ObservableProperty]
    private bool _changeHotKeyMode;

    /// <summary>修改快捷键模式下捕获到的按键组合。</summary>
    public string CapturedHotkey { get; set; } = "";

    public MainWindowViewModel(SettingsService settings)
    {
        _settings = settings;
    }

    public void AddSolver(ISolver solver)
    {
        var item = new SolverItemViewModel(solver)
        {
            IsVisible = !_settings.Settings.SkipList.Contains(solver.Name)
        };
        Solvers.Insert(0, item);
        if (item.IsVisible)
        {
            VisibleSolvers.Insert(0, item);
        }
    }

    public void SetSolverVisible(string name, bool visible)
    {
        CancelAutoSelect();
        var item = Solvers.FirstOrDefault(x => x.Name == name);
        if (item is null)
        {
            return;
        }

        item.IsVisible = visible;
        // 隐藏后若仍保持选中，Enter 会继续执行已隐藏的处理器
        if (!visible && item.IsSelected)
        {
            item.IsSelected = false;
        }
        if (!visible && ReferenceEquals(_hoveredSolver, item))
        {
            _hoveredSolver = null;
        }

        RebuildVisibleSolvers();
        RefreshPullOutState();
    }

    /// <summary>按 Solvers 顺序重建可见列表，保证工具栏顺序与间距稳定。</summary>
    private void RebuildVisibleSolvers()
    {
        VisibleSolvers.Clear();
        foreach (var item in Solvers.Where(x => x.IsVisible))
        {
            VisibleSolvers.Add(item);
        }
    }

    /// <summary>
    /// 读取剪贴板文本后，在后台为每个可见处理器打分并自动选中分数最高者。
    /// 新请求会取消旧请求；只有仍为最新一代的结果才能提交到界面。
    /// </summary>
    public async Task AutoSelectAsync(
        string clipboardText,
        CancellationToken cancellationToken = default)
    {
        if (ChangeHotKeyMode)
        {
            return;
        }
        string str = clipboardText.Trim().Replace("\r", "");
        if (str.Length == 0)
        {
            return;
        }
        BodyText = str;
        var strArr = str.Split('\n');
        bool jsonFlag = (str.StartsWith('[') && str.EndsWith(']')) || (str.StartsWith('{') && str.EndsWith('}'));
        // ObservableCollection 和 IsVisible 只在 UI 线程读取；后台仅使用不可变快照。
        SolverItemViewModel[] candidates = Solvers.Where(x => x.IsVisible).ToArray();

        int generation = ++_autoSelectGeneration;
        _autoSelectCancellation?.Cancel();
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _autoSelectCancellation = requestCancellation;

        try
        {
            SolverItemViewModel? best = await FindBestSolverAsync(
                candidates,
                str,
                strArr,
                jsonFlag,
                requestCancellation.Token);

            if (requestCancellation.IsCancellationRequested ||
                generation != _autoSelectGeneration ||
                !ReferenceEquals(_autoSelectCancellation, requestCancellation))
            {
                return;
            }

            foreach (var item in Solvers)
            {
                item.IsSelected = false;
            }
            if (best is { IsVisible: true })
            {
                best.IsSelected = true;
            }
            RefreshPullOutState();
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            // 新请求、隐藏窗口或人工操作取消了本次检查；不提交任何旧结果。
        }
        finally
        {
            if (ReferenceEquals(_autoSelectCancellation, requestCancellation))
            {
                _autoSelectCancellation = null;
            }
            requestCancellation.Dispose();
        }
    }

    public void CancelAutoSelect()
    {
        _autoSelectGeneration++;
        var cancellation = _autoSelectCancellation;
        _autoSelectCancellation = null;
        cancellation?.Cancel();
    }

    private async Task<SolverItemViewModel?> FindBestSolverAsync(
        SolverItemViewModel[] candidates,
        string str,
        string[] strArr,
        bool jsonFlag,
        CancellationToken cancellationToken)
    {
        await _solverCheckGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                SolverItemViewModel? best = null;
                int bestScore = 0;
                foreach (var item in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        int score = item.Solver.Check(str, strArr, jsonFlag, cancellationToken);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = item;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        AppLog.Error(
                            $"check 失败: name={item.Name}, describe={item.Describe}, " +
                            $"userScript={item.Solver.IsUserScript}, inputLen={str.Length}, lines={strArr.Length}, jsonFlag={jsonFlag}",
                            e);
                    }
                }
                return best;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _solverCheckGate.Release();
        }
    }

    public void SelectSolver(SolverItemViewModel target)
    {
        if (!target.IsVisible)
        {
            return;
        }

        CancelAutoSelect();
        foreach (var item in Solvers)
        {
            item.IsSelected = false;
        }
        target.IsSelected = true;
        RefreshPullOutState();
    }

    /// <summary>
    /// 悬停优先于默认选中：存在悬停项时只抽出悬停项；没有悬停项时抽出选中项。
    /// </summary>
    public void SetSolverHovered(SolverItemViewModel target, bool isHovered)
    {
        if (isHovered)
        {
            if (target.IsVisible)
            {
                _hoveredSolver = target;
            }
        }
        else if (ReferenceEquals(_hoveredSolver, target))
        {
            _hoveredSolver = null;
        }

        RefreshPullOutState();
    }

    /// <summary>
    /// 跟踪鼠标是否仍位于整个功能区。位于按钮间隙时不恢复默认选中项。
    /// </summary>
    public void SetSolverAreaHovered(bool isHovered)
    {
        _isSolverAreaHovered = isHovered;
        if (!isHovered)
        {
            _hoveredSolver = null;
        }
        RefreshPullOutState();
    }

    private void RefreshPullOutState()
    {
        foreach (var item in Solvers)
        {
            item.IsPulledOut = item.IsVisible && (_hoveredSolver is not null
                ? ReferenceEquals(item, _hoveredSolver)
                : !_isSolverAreaHovered && item.IsSelected);
        }
    }

    /// <summary>执行当前选中的处理器，返回结果文本（应写回剪贴板），无选中时返回 null。</summary>
    public string? Execute()
    {
        CancelAutoSelect();
        // 仅执行当前可见且选中的处理器，避免托盘隐藏后仍被触发
        var selected = Solvers.FirstOrDefault(x => x.IsSelected && x.IsVisible);
        if (selected is null)
        {
            return null;
        }
        string str = BodyText;
        bool jsonFlag = (str.StartsWith('[') && str.EndsWith(']')) || (str.StartsWith('{') && str.EndsWith('}'));
        var strArr = str.Split('\n');
        try
        {
            string result = selected.Solver.Transfer(str, strArr, jsonFlag);
            BodyText = result;
            if (selected.Solver.NextStep is { } nextStep)
            {
                // nextStep 指向已隐藏处理器时不切换，避免“看不见却已选中”
                var next = Solvers.FirstOrDefault(x => x.Name == nextStep && x.IsVisible);
                if (next is not null)
                {
                    SelectSolver(next);
                }
            }
            return result;
        }
        catch (Exception e)
        {
            AppLog.Error(
                $"transfer 失败: name={selected.Name}, describe={selected.Describe}, " +
                $"userScript={selected.Solver.IsUserScript}, inputLen={str.Length}, lines={strArr.Length}, jsonFlag={jsonFlag}",
                e);
            return null;
        }
    }

    partial void OnChangeHotKeyModeChanged(bool value)
    {
        if (value)
        {
            CancelAutoSelect();
        }
    }
}
