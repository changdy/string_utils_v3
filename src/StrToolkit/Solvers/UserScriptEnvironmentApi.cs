using System;

namespace StrToolkit.Solvers;

/// <summary>
/// 为 Jint 用户脚本桥接操作系统环境变量读取。
/// 业务功能和第三方依赖均由 JS 脚本实现。
/// </summary>
public sealed class UserScriptEnvironmentApi
{
    public string Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("环境变量名不能为空", nameof(name));
        }

        return Environment.GetEnvironmentVariable(name) ?? "";
    }
}
