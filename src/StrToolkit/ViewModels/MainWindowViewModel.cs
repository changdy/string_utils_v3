using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using StrToolkit.Services;
using StrToolkit.Solvers;

namespace StrToolkit.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly SettingsService _settings;

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

        RebuildVisibleSolvers();
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

    /// <summary>读取剪贴板文本后：为每个可见处理器打分并自动选中分数最高者。</summary>
    public void AutoSelect(string clipboardText)
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

        SolverItemViewModel? best = null;
        int bestScore = 0;
        foreach (var item in Solvers.Where(x => x.IsVisible))
        {
            try
            {
                int score = item.Solver.Check(str, strArr, jsonFlag);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }
            catch (Exception e)
            {
                AppLog.Error(
                    $"check 失败: name={item.Name}, describe={item.Describe}, " +
                    $"userScript={item.Solver.IsUserScript}, inputLen={str.Length}, lines={strArr.Length}, jsonFlag={jsonFlag}",
                    e);
            }
        }

        foreach (var item in Solvers)
        {
            item.IsSelected = false;
        }
        if (best is not null)
        {
            best.IsSelected = true;
        }
    }

    public void SelectSolver(SolverItemViewModel target)
    {
        if (!target.IsVisible)
        {
            return;
        }

        foreach (var item in Solvers)
        {
            item.IsSelected = false;
        }
        target.IsSelected = true;
    }

    /// <summary>执行当前选中的处理器，返回结果文本（应写回剪贴板），无选中时返回 null。</summary>
    public string? Execute()
    {
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
}
