using System;
using System.IO;
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
            "__strToolkitBase64Decode",
            new Func<string, string>(Base64Decode));
        engine.SetValue(
            "__strToolkitBase64Encode",
            new Func<string, string>(Base64Encode));
        engine.Execute("""
            (() => {
                const getRandomUInt32 = __strToolkitGetRandomUInt32;
                const decodeBase64 = __strToolkitBase64Decode;
                const encodeBase64 = __strToolkitBase64Encode;
                delete globalThis.__strToolkitGetRandomUInt32;
                delete globalThis.__strToolkitBase64Decode;
                delete globalThis.__strToolkitBase64Encode;
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
                Object.defineProperty(globalThis, "base64Decode", {
                    value: value => {
                        const text = String(value).replace(/\s/g, "");
                        const hasPadding = text.includes("=");
                        if (!/^[A-Za-z0-9+/]*={0,2}$/.test(text) ||
                            (hasPadding && text.length % 4 !== 0) ||
                            text.length % 4 === 1) {
                            throw new TypeError(
                                "base64Decode 输入不是有效的 Base64");
                        }
                        return decodeBase64(text);
                    },
                    writable: false,
                    configurable: false,
                    enumerable: true
                });
                Object.defineProperty(globalThis, "base64Encode", {
                    value: value => encodeBase64(String(value)),
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

    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string Base64Encode(string value)
    {
        return Convert.ToBase64String(StrictUtf8.GetBytes(value));
    }

    private static string Base64Decode(string value)
    {
        // JS 侧已完成字符集与填充校验；这里补齐缺失的填充后按 UTF-8 解码。
        var normalized = new StringBuilder(value);
        int remainder = normalized.Length % 4;
        if (remainder > 1)
        {
            normalized.Append('=', 4 - remainder);
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(normalized.ToString());
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "base64Decode 输入不是有效的 Base64",
                nameof(value),
                exception);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException(
                "base64Decode 结果不是有效的 UTF-8 文本",
                nameof(value),
                exception);
        }
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
