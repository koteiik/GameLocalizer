using System.ComponentModel;
using System.Runtime.CompilerServices;
using GameLocalizer.Core.Validation;
using GameLocalizer.Core.Models;
namespace GameLocalizer.UI.ViewModels;
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { field = value; Changed(name); }
}
public sealed class TranslationRow : Observable
{
    public string ErrorType { get; init; } = "";
    public string ErrorMessage { get; init; } = "";
    public string RetryStatus { get; init; } = "";
    public long Id { get; init; }
    public required string Original { get; init; }
    public required string File { get; init; }
    public string PhysicalSourceFile { get; init; } = "";
    public bool Applied { get; init; }
    public string FileToolTip => PhysicalSourceFile.Length > 0 ? PhysicalSourceFile : File;
    public string FileName => System.IO.Path.GetFileName(File);
    public string StatusLabel => Applied ? "Применено" : Status switch { "FromMemory" => "Память", "Translated" => "Переведено", "Manual" => "Вручную", "SourceChanged" => "Источник изменён", "ValidationError" or "Failed" => "Ошибка", "NotTranslated" => "Не переведено", "Queued" => "В очереди", "Translating" => "Перевод…", "Cancelled" => "Отменено", _ => Status };
    public string LocalizationSlot { get; init; } = "";
    public string Slot => LocalizationSlot.Length > 0 ? LocalizationSlot : GameLocalizer.Core.Localization.ActiveLocalizationResolver.SlotFromPath(File);
    public required string Key { get; init; }
    public string Context { get; init; } = "";
    public double Confidence { get; init; }
    private string russian = "";
    public TextCategory Category { get; init; } = TextCategory.Possible;
    public bool CanSelect => Category is not (TextCategory.Technical or TextCategory.UnsupportedUI);
    private bool selected;
    public bool Selected { get => selected; set => Set(ref selected, value && CanSelect); }
    public TranslationStatus State { get; set; } = TranslationStatus.NotTranslated;
    public string Russian { get => russian; set { State = string.IsNullOrWhiteSpace(value) ? TranslationStatus.NotTranslated : TranslationStatus.Manual; Set(ref russian, value); Changed(nameof(Status)); Changed(nameof(StatusLabel)); } }
    public string Status => !string.IsNullOrWhiteSpace(Russian) && !new TranslationValidator().Validate(Original, Russian, out _) ? "ValidationError" : State.ToString();
}
