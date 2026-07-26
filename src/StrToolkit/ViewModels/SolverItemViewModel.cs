using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;
using StrToolkit.Services;
using StrToolkit.Solvers;

namespace StrToolkit.ViewModels;

public partial class SolverItemViewModel : ObservableObject
{
    public ISolver Solver { get; }

    public string Name => Solver.Name;
    public string Describe => Solver.Describe;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isVisible = true;

    public IImage? Icon { get; }

    public SolverItemViewModel(ISolver solver)
    {
        Solver = solver;
        Icon = LoadIcon(solver);
    }

    private static IImage? LoadIcon(ISolver solver)
    {
        try
        {
            if (solver.IsUserScript && solver.IconBasePath is not null)
            {
                foreach (var ext in new[] { ".svg", ".png" })
                {
                    string p = solver.IconBasePath + ext;
                    if (File.Exists(p))
                    {
                        if (ext == ".svg")
                        {
                            var source = SvgSource.Load(p);
                            return source is null ? null : new SvgImage { Source = source };
                        }
                        return new Avalonia.Media.Imaging.Bitmap(p);
                    }
                }
                return null;
            }

            var uri = new Uri($"avares://StrToolkit/Assets/fun-icon/{solver.Name}.svg");
            if (AssetLoader.Exists(uri))
            {
                var source = SvgSource.Load(uri.ToString());
                return source is null ? null : new SvgImage { Source = source };
            }
        }
        catch (Exception e)
        {
            AppLog.Error($"加载图标失败: name={solver.Name}", e);
        }
        return null;
    }
}
