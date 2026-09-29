using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Models;
namespace GameLocalizer.Infrastructure.TranslationProviders;

public sealed class MockTranslationProvider : ITranslationProvider
{
    public string Name => "Mock";
    private static readonly Dictionary<string, string> Dictionary = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Play"] = "Играть", ["Start Game"] = "Начать игру", ["Continue"] = "Продолжить", ["Settings"] = "Настройки",
        ["Exit"] = "Выход", ["Quest completed"] = "Задание выполнено", ["Save Game"] = "Сохранить игру",
        ["New Game"] = "Новая игра", ["Load Game"] = "Загрузить игру", ["I don't think we should go there."] = "Не думаю, что нам стоит туда идти."
    };
    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>();
        foreach (var item in request.Batch.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.Add(item.Id, Dictionary.GetValueOrDefault(item.Text, "[ДЕМО] " + item.Text));
        }
        return Task.FromResult(new TranslationResult(values));
    }
}
