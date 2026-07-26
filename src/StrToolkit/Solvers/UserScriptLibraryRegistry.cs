using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Jint;

namespace StrToolkit.Solvers;

/// <summary>
/// 向每个隔离的 Jint 引擎安装应用内置的 JavaScript 库。
/// 用户脚本只能通过白名单 require 获取这些库，不支持 Node.js 模块解析。
/// </summary>
internal static class UserScriptLibraryRegistry
{
    private const string CryptoJsResourceName =
        "StrToolkit.Assets.JsRuntime.crypto-js.min.js";

    private static readonly Lazy<string> CryptoJsSource = new(
        () => ReadEmbeddedText(CryptoJsResourceName));

    public static void Install(Engine engine)
    {
        engine.SetValue(
            "__strToolkitGetRandomUInt32",
            new Func<double>(GetRandomUInt32));
        engine.Execute("""
            (() => {
                const getRandomUInt32 = __strToolkitGetRandomUInt32;
                delete globalThis.__strToolkitGetRandomUInt32;
                const crypto = Object.freeze({
                    getRandomValues(array) {
                        if (!array || typeof array.length !== "number") {
                            throw new TypeError("crypto.getRandomValues 需要 TypedArray");
                        }
                        for (let index = 0; index < array.length; index++) {
                            array[index] = getRandomUInt32();
                        }
                        return array;
                    }
                });
                Object.defineProperty(globalThis, "crypto", {
                    value: crypto,
                    writable: false,
                    configurable: false,
                    enumerable: true
                });
            })();
            """);

        engine.Execute(CryptoJsSource.Value);
        if (engine.GetValue("CryptoJS").IsUndefined())
        {
            throw new InvalidOperationException("内置 crypto-js 初始化失败");
        }

        engine.Execute("""
            (() => {
                const modules = Object.freeze({
                    "crypto-js": CryptoJS
                });
                const require = specifier => {
                    const name = String(specifier);
                    if (!Object.prototype.hasOwnProperty.call(modules, name)) {
                        throw new Error(
                            `不支持的内置 JavaScript 模块: ${name}`);
                    }
                    return modules[name];
                };
                Object.defineProperty(globalThis, "require", {
                    value: require,
                    writable: false,
                    configurable: false,
                    enumerable: true
                });
            })();
            """);
    }

    private static double GetRandomUInt32()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt32(bytes);
    }

    private static string ReadEmbeddedText(string resourceName)
    {
        Assembly assembly = typeof(UserScriptLibraryRegistry).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"找不到内置 JavaScript 资源: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
