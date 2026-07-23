using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace StrToolkit.Services.JsonHero;

/// <summary>JSON Hero 文档的内存存储、过期清理与容量淘汰。</summary>
internal sealed class DocumentStore
{
    private const int MaxDocuments = 500;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);
    private static readonly char[] IdAlphabet =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".ToCharArray();

    private readonly ConcurrentDictionary<string, StoredDocument> _documents = new();
    private readonly TimeProvider _timeProvider;

    public DocumentStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // 保持现有 health 语义：只报告当前字典数量，不因探活主动清除过期项。
    public int Count => _documents.Count;

    public StoredDocument CreateRaw(string title, string contents, TimeSpan? ttl, bool readOnly)
    {
        using var _ = JsonDocument.Parse(contents);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var document = new StoredDocument(
            CreateId(),
            title,
            readOnly,
            DocumentType.Raw,
            contents,
            null,
            now,
            now.Add(ttl ?? DefaultTtl));
        Store(document);
        return document;
    }

    public StoredDocument CreateUrl(string title, string url, TimeSpan? ttl, bool readOnly)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var document = new StoredDocument(
            CreateId(),
            title,
            readOnly,
            DocumentType.Url,
            null,
            url,
            now,
            now.Add(ttl ?? DefaultTtl));
        Store(document);
        return document;
    }

    public bool TryGet(string id, out StoredDocument document)
    {
        if (_documents.TryGetValue(id, out document!) && document.ExpiresAt > _timeProvider.GetUtcNow())
        {
            return true;
        }

        _documents.TryRemove(id, out _);
        document = null!;
        return false;
    }

    public DocumentMutationResult TryUpdateTitle(string id, string title, out StoredDocument document)
    {
        while (TryGet(id, out var current))
        {
            if (current.ReadOnly)
            {
                document = current;
                return DocumentMutationResult.ReadOnly;
            }

            var updated = current with { Title = title };
            if (_documents.TryUpdate(id, updated, current))
            {
                document = updated;
                return DocumentMutationResult.Success;
            }
        }

        document = null!;
        return DocumentMutationResult.NotFound;
    }

    public DocumentMutationResult TryDelete(string id)
    {
        while (TryGet(id, out var current))
        {
            if (current.ReadOnly)
            {
                return DocumentMutationResult.ReadOnly;
            }
            if (_documents.TryRemove(id, out _))
            {
                return DocumentMutationResult.Success;
            }
        }
        return DocumentMutationResult.NotFound;
    }

    private void Store(StoredDocument document)
    {
        CleanExpiredDocuments();
        if (_documents.Count >= MaxDocuments)
        {
            foreach (string id in _documents.Values
                         .OrderBy(item => item.CreatedAt)
                         .Take(Math.Max(1, _documents.Count - MaxDocuments + 1))
                         .Select(item => item.Id))
            {
                _documents.TryRemove(id, out _);
            }
        }
        _documents[document.Id] = document;
    }

    private void CleanExpiredDocuments()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (var item in _documents)
        {
            if (item.Value.ExpiresAt <= now)
            {
                _documents.TryRemove(item.Key, out _);
            }
        }
    }

    private static string CreateId()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[12];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = IdAlphabet[bytes[i] % IdAlphabet.Length];
        }
        return new string(chars);
    }
}

internal enum DocumentMutationResult
{
    Success,
    NotFound,
    ReadOnly
}

internal enum DocumentType
{
    Raw,
    Url
}

internal sealed record StoredDocument(
    string Id,
    string Title,
    bool ReadOnly,
    DocumentType Type,
    string? Contents,
    string? Url,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
