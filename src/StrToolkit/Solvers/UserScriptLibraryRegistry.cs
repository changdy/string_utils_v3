using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
    private const string LodashResourceName =
        "StrToolkit.Assets.JsRuntime.lodash.min.js";
    private const string DayJsResourceName =
        "StrToolkit.Assets.JsRuntime.dayjs.min.js";

    private static readonly Lazy<string> CryptoJsSource = new(
        () => ReadEmbeddedText(CryptoJsResourceName));
    private static readonly Lazy<string> LodashSource = new(
        () => ReadEmbeddedText(LodashResourceName));
    private static readonly Lazy<string> DayJsSource = new(
        () => ReadEmbeddedText(DayJsResourceName));

    public static void Install(Engine engine)
    {
        engine.SetValue(
            "__strToolkitGetRandomUInt32",
            new Func<double>(GetRandomUInt32));
        engine.SetValue(
            "__strToolkitAtob",
            new Func<string, string>(Atob));
        engine.SetValue(
            "__strToolkitBtoa",
            new Func<string, string>(Btoa));
        engine.Execute("""
            (() => {
                const getRandomUInt32 = __strToolkitGetRandomUInt32;
                const decodeBase64 = __strToolkitAtob;
                const encodeBase64 = __strToolkitBtoa;
                delete globalThis.__strToolkitGetRandomUInt32;
                delete globalThis.__strToolkitAtob;
                delete globalThis.__strToolkitBtoa;
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
                Object.defineProperty(globalThis, "atob", {
                    value: value => {
                        const text = String(value)
                            .replace(/[ \t\n\f\r]/g, "");
                        const paddingIndex = text.indexOf("=");
                        const body = paddingIndex < 0
                            ? text
                            : text.slice(0, paddingIndex);
                        const padding = paddingIndex < 0
                            ? ""
                            : text.slice(paddingIndex);
                        if (!/^[A-Za-z0-9+/]*$/.test(body) ||
                            (padding && !/^={1,2}$/.test(padding)) ||
                            (padding && text.length % 4 !== 0) ||
                            body.length % 4 === 1) {
                            throw new TypeError(
                                "atob 输入不是有效的 Base64");
                        }
                        return decodeBase64(text);
                    },
                    writable: false,
                    configurable: false,
                    enumerable: true
                });
                Object.defineProperty(globalThis, "btoa", {
                    value: value => {
                        const text = String(value);
                        for (let index = 0;
                            index < text.length;
                            index++) {
                            if (text.charCodeAt(index) > 0xff) {
                                throw new TypeError(
                                    "btoa 仅接受 Latin-1 字符；" +
                                    "请先将 Unicode 文本编码为 UTF-8 字节串");
                            }
                        }
                        return encodeBase64(text);
                    },
                    writable: false,
                    configurable: false,
                    enumerable: true
                });
            })();
            """);

        ExecuteLibrary(engine, LodashSource.Value, "_", "lodash");
        ExecuteLibrary(engine, DayJsSource.Value, "dayjs", "dayjs");
        ExecuteLibrary(engine, CryptoJsSource.Value, "CryptoJS", "crypto-js");

        engine.Execute("""
            (() => {
                const modules = Object.freeze({
                    "crypto-js": CryptoJS,
                    "dayjs": dayjs,
                    "lodash": _
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

    private static void ExecuteLibrary(
        Engine engine,
        string source,
        string globalName,
        string moduleName)
    {
        engine.Execute(source);
        if (engine.GetValue(globalName).IsUndefined())
        {
            throw new InvalidOperationException(
                $"内置 {moduleName} 初始化失败");
        }
    }

    private static double GetRandomUInt32()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt32(bytes);
    }

    private static string Atob(string value)
    {
        var normalized = new StringBuilder(value.Length + 2);
        foreach (char character in value)
        {
            if (character is ' ' or '\t' or '\n' or '\f' or '\r')
            {
                continue;
            }
            normalized.Append(character);
        }

        int paddingIndex = normalized.ToString().IndexOf('=');
        if (paddingIndex >= 0)
        {
            int paddingLength = normalized.Length - paddingIndex;
            if (normalized.Length % 4 != 0 ||
                paddingLength > 2 ||
                normalized.ToString(paddingIndex, paddingLength)
                    .Any(character => character != '='))
            {
                throw new ArgumentException("atob 输入不是有效的 Base64");
            }
        }
        else
        {
            int remainder = normalized.Length % 4;
            if (remainder == 1)
            {
                throw new ArgumentException("atob 输入不是有效的 Base64");
            }
            if (remainder > 1)
            {
                normalized.Append('=', 4 - remainder);
            }
        }

        try
        {
            return Encoding.Latin1.GetString(
                Convert.FromBase64String(normalized.ToString()));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "atob 输入不是有效的 Base64",
                nameof(value),
                exception);
        }
    }

    private static string Btoa(string value)
    {
        foreach (char character in value)
        {
            if (character > byte.MaxValue)
            {
                throw new ArgumentException(
                    "btoa 仅接受 Latin-1 字符；请先将 Unicode 文本编码为 UTF-8 字节串",
                    nameof(value));
            }
        }
        return Convert.ToBase64String(Encoding.Latin1.GetBytes(value));
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
