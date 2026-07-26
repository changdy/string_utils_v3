namespace StrToolkit.Solvers;

/// <summary>
/// 文本处理器契约，对应 Electron 版的 solver 对象。
/// check 返回匹配分数（越高越优先被自动选中），transfer 执行转换。
/// </summary>
public interface ISolver
{
    string Name { get; }

    string Describe { get; }

    /// <summary>处理完成后自动切换到的处理器名称，可为 null。</summary>
    string? NextStep { get; }

    /// <summary>是否来自用户脚本。</summary>
    bool IsUserScript => false;

    /// <summary>用户脚本图标路径（无扩展名），内置处理器为 null。</summary>
    string? IconBasePath => null;

    int Check(string logs, string[] arr, bool jsonFlag);

    string Transfer(string logs, string[] arr, bool jsonFlag);
}
