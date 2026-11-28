using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Glance.Core;

/// <summary>Offline EN↔ZH via Opus-MT ONNX (greedy decode + Marian SentencePiece/vocab).</summary>
public sealed class OfflineTranslateClient : IDisposable
{
    private const int DecoderStartId = 65000; // pad / decoder_start
    private const int EosId = 0;              // </s> in Marian vocab.json
    private const int PadId = 65000;
    private const int UnkId = 1;
    private const int MaxNewTokens = 128;
    private const int NumLayers = 6;
    private const int NumHeads = 8;
    private const int HeadDim = 64;

    private static readonly Regex SentenceSplit = new(
        @"(?<=[。！？.!?；;])\s+|\n+",
        RegexOptions.Compiled);

    private readonly OfflineMtPack _pack;
    private readonly object _gate = new();
    private DirectionSessions? _enZh;
    private DirectionSessions? _zhEn;
    private bool _disposed;

    public OfflineTranslateClient(OfflineMtPack? pack = null)
        => _pack = pack ?? new OfflineMtPack();

    public OfflineMtPack Pack => _pack;

    public Task<TextTranslationResult> TranslateAsync(
        string text,
        string from,
        string to,
        TranslatorSettings settings,
        CancellationToken ct = default)
    {
        _ = settings;
        return Task.Run(() => TranslateCore(text, from, to, ct), ct);
    }

    private TextTranslationResult TranslateCore(
        string text, string from, string to, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        text = text.Trim();
        if (text.Length == 0)
            return new TextTranslationResult { TranslatedText = "" };

        if (!_pack.IsInstalled())
            throw new InvalidOperationException("未安装离线语言包，请到设置中下载「英↔中」语言包");

        var direction = ResolveDirection(text, from, to);
        var sessions = GetOrLoad(direction);
        var parts = SplitSentences(text);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();
            var piece = part.Trim();
            if (piece.Length == 0) continue;
            var translated = TranslateOne(sessions, piece, ct);
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(translated);
        }

        return new TextTranslationResult { TranslatedText = sb.ToString().Trim() };
    }

    private static string ResolveDirection(string text, string from, string to)
    {
        static bool IsZh(string lang) =>
            lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            || lang is "zh-CHS" or "zh-CHT" or "zh-CN" or "zh-TW";
        static bool IsEn(string lang) =>
            lang.Equals("en", StringComparison.OrdinalIgnoreCase)
            || lang.StartsWith("en-", StringComparison.OrdinalIgnoreCase);

        var fromLang = from;
        var toLang = to;
        if (fromLang is "auto" or "")
            fromLang = LooksMostlyChinese(text) ? "zh" : "en";

        if (IsZh(fromLang) && (IsEn(toLang) || toLang is "auto" or ""))
            return "zh-en";
        if (IsEn(fromLang) && (IsZh(toLang) || toLang is "auto" or ""))
            return "en-zh";
        if (IsZh(toLang) && !IsZh(fromLang))
            return "en-zh";
        if (IsEn(toLang) && !IsEn(fromLang))
            return "zh-en";

        return LooksMostlyChinese(text) ? "zh-en" : "en-zh";
    }

    private static bool LooksMostlyChinese(string text)
    {
        var han = 0;
        var letter = 0;
        foreach (var ch in text)
        {
            if (ch is >= '\u4e00' and <= '\u9fff') han++;
            else if (char.IsLetter(ch)) letter++;
        }
        return han > 0 && han >= letter;
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        if (text.Length <= 200)
            return [text];
        var parts = SentenceSplit.Split(text).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        return parts.Length == 0 ? [text] : parts;
    }

    private DirectionSessions GetOrLoad(string direction)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (direction == "en-zh")
                return _enZh ??= DirectionSessions.Load(_pack.DirectionPath("en-zh"));
            if (direction == "zh-en")
                return _zhEn ??= DirectionSessions.Load(_pack.DirectionPath("zh-en"));
            throw new InvalidOperationException("离线引擎仅支持英↔中");
        }
    }

    private static string TranslateOne(DirectionSessions s, string text, CancellationToken ct)
    {
        var ids = s.EncodeMarian(text);
        if (ids.Length == 0)
            ids = [EosId];
        if (ids.Length > 480)
            ids = ids.Take(479).Append(EosId).ToArray();

        var inputIds = new DenseTensor<long>(ids, [1, ids.Length]);
        var attnData = Enumerable.Repeat(1L, ids.Length).ToArray();
        var attn = new DenseTensor<long>(attnData, [1, ids.Length]);

        DenseTensor<float> hidden;
        using (var encOut = s.Encoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attn),
        }))
        {
            hidden = CopyTensor(encOut.First(o => o.Name == "last_hidden_state").AsTensor<float>());
        }

        var generated = new List<long>();
        long nextToken = DecoderStartId;
        PastCache? past = null;

        for (var step = 0; step < MaxNewTokens; step++)
        {
            ct.ThrowIfCancellationRequested();
            var useCache = past is not null;
            var feeds = BuildDecoderFeeds(nextToken, attnData, hidden, past, useCache);

            using var decOut = s.Decoder.Run(feeds);
            var logits = decOut.First(o => o.Name == "logits").AsTensor<float>();
            nextToken = ArgMaxLast(logits, forbidPad: true);
            if (nextToken == EosId)
                break;
            if (nextToken == PadId)
                continue;

            // Greedy+cache can loop on one piece; stop before dumping repeats.
            if (generated.Count > 0 && generated[^1] == nextToken)
                break;

            generated.Add(nextToken);

            // Keep encoder KV from first step — cache-branch present.encoder.* can be bogus.
            var nextPast = ReadPresent(decOut, copyEncoder: past is null);
            if (past is not null)
            {
                for (var i = 0; i < NumLayers; i++)
                {
                    nextPast.EncoderKeys[i] = past.EncoderKeys[i];
                    nextPast.EncoderValues[i] = past.EncoderValues[i];
                }
            }
            past = nextPast;
        }

        if (generated.Count == 0)
            return "";

        return s.DecodeMarian(generated);
    }

    private static List<NamedOnnxValue> BuildDecoderFeeds(
        long nextToken,
        long[] attnData,
        DenseTensor<float> hidden,
        PastCache? past,
        bool useCache)
    {
        var feeds = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids",
                new DenseTensor<long>(new[] { nextToken }, [1, 1])),
            NamedOnnxValue.CreateFromTensor("encoder_attention_mask",
                new DenseTensor<long>(attnData.ToArray(), [1, attnData.Length])),
            NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden),
            NamedOnnxValue.CreateFromTensor("use_cache_branch",
                new DenseTensor<bool>(new[] { useCache }, [1])),
        };

        for (var layer = 0; layer < NumLayers; layer++)
        {
            feeds.Add(NamedOnnxValue.CreateFromTensor(
                $"past_key_values.{layer}.decoder.key", past?.DecoderKeys[layer] ?? EmptyKv()));
            feeds.Add(NamedOnnxValue.CreateFromTensor(
                $"past_key_values.{layer}.decoder.value", past?.DecoderValues[layer] ?? EmptyKv()));
            feeds.Add(NamedOnnxValue.CreateFromTensor(
                $"past_key_values.{layer}.encoder.key", past?.EncoderKeys[layer] ?? EmptyKv()));
            feeds.Add(NamedOnnxValue.CreateFromTensor(
                $"past_key_values.{layer}.encoder.value", past?.EncoderValues[layer] ?? EmptyKv()));
        }

        return feeds;
    }

    private static DenseTensor<float> EmptyKv()
        => new DenseTensor<float>(new[] { 1, NumHeads, 0, HeadDim });

    private static PastCache ReadPresent(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        bool copyEncoder)
    {
        var past = new PastCache();
        for (var i = 0; i < NumLayers; i++)
        {
            past.DecoderKeys[i] = CopyTensor(results.First(o => o.Name == $"present.{i}.decoder.key").AsTensor<float>());
            past.DecoderValues[i] = CopyTensor(results.First(o => o.Name == $"present.{i}.decoder.value").AsTensor<float>());
            if (copyEncoder)
            {
                past.EncoderKeys[i] = CopyTensor(results.First(o => o.Name == $"present.{i}.encoder.key").AsTensor<float>());
                past.EncoderValues[i] = CopyTensor(results.First(o => o.Name == $"present.{i}.encoder.value").AsTensor<float>());
            }
        }
        return past;
    }

    private static DenseTensor<float> CopyTensor(Tensor<float> src)
    {
        var dims = src.Dimensions.ToArray();
        return new DenseTensor<float>(src.ToArray(), dims);
    }

    private static long ArgMaxLast(Tensor<float> logits, bool forbidPad)
    {
        var dims = logits.Dimensions.ToArray();
        var seq = dims.Length >= 2 ? dims[^2] : 1;
        var vocab = dims[^1];
        var offset = (seq - 1) * vocab;
        var best = 0L;
        var bestScore = float.NegativeInfinity;
        for (var i = 0; i < vocab; i++)
        {
            if (forbidPad && i == PadId) continue;
            var score = logits.GetValue(offset + i);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return best;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _enZh?.Dispose();
            _zhEn?.Dispose();
            _enZh = null;
            _zhEn = null;
        }
    }

    private sealed class PastCache
    {
        public DenseTensor<float>[] DecoderKeys { get; } = new DenseTensor<float>[NumLayers];
        public DenseTensor<float>[] DecoderValues { get; } = new DenseTensor<float>[NumLayers];
        public DenseTensor<float>[] EncoderKeys { get; } = new DenseTensor<float>[NumLayers];
        public DenseTensor<float>[] EncoderValues { get; } = new DenseTensor<float>[NumLayers];
    }

    private sealed class DirectionSessions : IDisposable
    {
        public required InferenceSession Encoder { get; init; }
        public required InferenceSession Decoder { get; init; }
        public required SentencePieceTokenizer SourceSp { get; init; }
        public required Dictionary<string, int> TokenToId { get; init; }
        public required Dictionary<int, string> IdToToken { get; init; }

        public long[] EncodeMarian(string text)
        {
            var tokens = SourceSp.EncodeToTokens(text, out _, addBeginningOfSentence: false,
                addEndOfSentence: false, considerPreTokenization: true, considerNormalization: true);
            var ids = new List<long>(tokens.Count + 1);
            foreach (var token in tokens)
            {
                var piece = token.Value;
                ids.Add(TokenToId.TryGetValue(piece, out var id) ? id : UnkId);
            }
            ids.Add(EosId);
            return ids.ToArray();
        }

        public string DecodeMarian(IReadOnlyList<long> ids)
        {
            var sb = new StringBuilder();
            foreach (var id in ids)
            {
                if (id is EosId or PadId) continue;
                if (!IdToToken.TryGetValue((int)id, out var piece)) continue;
                if (piece is "<unk>" or "<pad>" or "</s>" or "<s>") continue;
                sb.Append(piece);
            }

            var s = sb.ToString().Replace('▁', ' ').Trim();
            s = Regex.Replace(s, @"(?<=[\u4e00-\u9fff])\s+(?=[\u4e00-\u9fff])", "");
            s = Regex.Replace(s, @"\s{2,}", " ");
            return s.Trim();
        }

        public static DirectionSessions Load(string dir)
        {
            var encPath = Path.Combine(dir, "encoder_model_quantized.onnx");
            var decPath = Path.Combine(dir, "decoder_model_merged_quantized.onnx");
            var srcSp = Path.Combine(dir, "source.spm");
            var vocabPath = Path.Combine(dir, "vocab.json");
            foreach (var p in new[] { encPath, decPath, srcSp, vocabPath })
            {
                if (!File.Exists(p))
                    throw new FileNotFoundException("离线语言包文件缺失: " + p);
            }

            static SessionOptions MakeOpts() => new()
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL,
            };

            var tokenToId = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabPath))
                ?? throw new InvalidOperationException("vocab.json 无效: " + vocabPath);
            var idToToken = new Dictionary<int, string>(tokenToId.Count);
            foreach (var (token, id) in tokenToId)
                idToToken[id] = token;

            SentencePieceTokenizer sourceSp;
            using (var srcStream = File.OpenRead(srcSp))
                sourceSp = SentencePieceTokenizer.Create(srcStream, addBeginningOfSentence: false, addEndOfSentence: false);

            return new DirectionSessions
            {
                Encoder = new InferenceSession(encPath, MakeOpts()),
                Decoder = new InferenceSession(decPath, MakeOpts()),
                SourceSp = sourceSp,
                TokenToId = tokenToId,
                IdToToken = idToToken,
            };
        }

        public void Dispose()
        {
            Encoder.Dispose();
            Decoder.Dispose();
        }
    }
}
