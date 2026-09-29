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
    public long Id { get; init; }
    public required string Original { get; init; }
    public required string File { get; init; }
    public required string Key { get; init; }
    public string Context { get; init; } = "";
    public double Confidence { get; init; }
    private string russian = "";
    public TextCategory Category { get; init; } = TextCategory.Possible;
    public bool CanSelect => Category != TextCategory.Technical;
    private bool selected;
    public bool Selected { get => selected; set => Set(ref selected, value && CanSelect); }
    public TranslationStatus State { get; set; } = TranslationStatus.NotTranslated;
    public string Russian { get => russian; set { State = string.IsNullOrWhiteSpace(value) ? TranslationStatus.NotTranslated : TranslationStatus.Manual; Set(ref russian, value); Changed(nameof(Status)); } }
    public string Status => !string.IsNullOrWhiteSpace(Russian) && !new TranslationValidator().Validate(Original, Russian, out _) ? "ValidationError" : State.ToString();
}
