using System.ComponentModel;
using System.Runtime.CompilerServices;
using GameLocalizer.Core.Validation;
namespace GameLocalizer.UI.ViewModels;
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { field = value; Changed(name); }
}
public sealed class TranslationRow : Observable
{
    public required string Original { get; init; }
    public required string File { get; init; }
    public required string Key { get; init; }
    public string Context { get; init; } = "";
    public double Confidence { get; init; }
    private string russian = "";
    private bool selected = true;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public string Russian { get => russian; set { Set(ref russian, value); Changed(nameof(Status)); } }
    public string Status => string.IsNullOrWhiteSpace(Russian) ? "Не переведено" : new TranslationValidator().Validate(Original, Russian, out _) ? "Готово" : "Validation Error";
}
