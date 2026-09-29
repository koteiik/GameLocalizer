using System.Text.Json;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
namespace GameLocalizer.Infrastructure.TranslationProviders;

/// <summary>Local-only Marian encoder/decoder. No network client or model hub integration.</summary>
public sealed class MarianOnnxRuntime : ITranslationRuntime
{
    private InferenceSession? encoder, decoder;
    private SentencePieceTokenizer? tokenizer;
    private Dictionary<string, int>? vocabulary;
    private string[]? pieces;
    public bool IsLoaded => encoder != null && decoder != null;
    public string Device { get; private set; } = "CPU";
    public int LoadCount { get; private set; }
    public int InferenceCount { get; private set; }
    public Task LoadAsync(string directory, TranslationDevice device, CancellationToken ct) => Task.Run(() =>
    {
        Unload(); ct.ThrowIfCancellationRequested();
        try
        {
            using var source = File.OpenRead(Path.Combine(directory, "source.spm"));
            tokenizer = SentencePieceTokenizer.Create(source, false, false);
            vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(directory, "vocab.json")))!;
            pieces = new string[vocabulary.Values.Max() + 1]; foreach (var pair in vocabulary) pieces[pair.Value] = pair.Key;
            using var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8), InterOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, EnableMemoryPattern = device == TranslationDevice.CPU, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            if (device == TranslationDevice.GPU) options.AppendExecutionProvider_DML(0);
            encoder = new InferenceSession(Path.Combine(directory, "onnx", "encoder_model_quantized.onnx"), options);
            decoder = new InferenceSession(Path.Combine(directory, "onnx", "decoder_model_quantized.onnx"), options);
            ct.ThrowIfCancellationRequested(); Device = device == TranslationDevice.GPU ? "GPU (DirectML; часть операций может выполняться на CPU)" : "CPU"; LoadCount++;
        }
        catch { Unload(); throw; }
    }, ct);
    public Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> text, CancellationToken ct) => Task.Run<IReadOnlyList<string>>(() =>
    {
        if (!IsLoaded || tokenizer == null || vocabulary == null || pieces == null) throw new InvalidOperationException("Model not loaded");
        if (text.Count == 0) return [];
        ct.ThrowIfCancellationRequested(); InferenceCount++;
        var tokenized = text.Select(s => tokenizer.EncodeToTokens(s, out _).Select(t => (long)(vocabulary.TryGetValue(t.Value, out var id) ? id : 1)).Append(0L).ToArray()).ToArray();
        if (tokenized.Any(t => t.Length > 256)) throw new InvalidDataException("Строка превышает 255 токенов модели. Разбейте текст на более короткие строки.");
        int batch = text.Count, length = tokenized.Max(t => t.Length);
        var ids = new DenseTensor<long>(new[] { batch, length }); var mask = new DenseTensor<long>(new[] { batch, length });
        for (var b = 0; b < batch; b++) for (var p = 0; p < length; p++) { ids[b, p] = p < tokenized[b].Length ? tokenized[b][p] : 62517; mask[b, p] = p < tokenized[b].Length ? 1 : 0; }
        using var run = new RunOptions(); using var cancel = ct.Register(() => run.Terminate = true);
        try
        {
            using var encoded = encoder!.Run([NamedOnnxValue.CreateFromTensor("input_ids", ids), NamedOnnxValue.CreateFromTensor("attention_mask", mask)], ["last_hidden_state"], run);
            var hidden = encoded.First().AsTensor<float>();
            var output = Enumerable.Range(0, batch).Select(_ => new List<long> { 62517 }).ToArray();
            var finished = new bool[batch];
            var maximum = Math.Min(256, Math.Max(32, length * 3));
            for (var step = 0; step < maximum && finished.Any(f => !f); step++)
            {
                ct.ThrowIfCancellationRequested();
                var decoderIds = new DenseTensor<long>(new[] { batch, step + 1 });
                for (var b = 0; b < batch; b++) for (var p = 0; p <= step; p++) decoderIds[b, p] = output[b][p];
                using var decoded = decoder!.Run([NamedOnnxValue.CreateFromTensor("input_ids", decoderIds), NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask), NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden)], ["logits"], run);
                var logits = decoded.First().AsTensor<float>();
                for (var b = 0; b < batch; b++)
                {
                    var best = 0; var score = float.NegativeInfinity;
                    if (!finished[b]) for (var token = 0; token < pieces.Length - 1; token++) if (logits[b, step, token] > score) { score = logits[b, step, token]; best = token; }
                    output[b].Add(best); if (best == 0) finished[b] = true;
                }
            }
            if (finished.Any(f => !f)) throw new InvalidDataException("Модель достигла лимита длины перевода; результат не сохранён.");
            return output.Select(row => string.Concat(row.Skip(1).TakeWhile(id => id != 0).Select(id => pieces[(int)id])).Replace("▁", " ").Trim()).ToArray();
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }, ct);
    public void Unload() { decoder?.Dispose(); decoder = null; encoder?.Dispose(); encoder = null; tokenizer = null; vocabulary = null; pieces = null; }
    public void Dispose() => Unload();
}
