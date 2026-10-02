using System.Windows;
using System.Windows.Controls;
using GameLocalizer.Core.Models;
namespace GameLocalizer.UI.Views;

public sealed record ApplySummary(ApplySelectionSummary Selection,int Files,string Description,bool ShowCombinedWarning);
public sealed record ApplySummaryDecision(string Choice,bool DontAskAgain=false);
public sealed class ApplySummaryWindow : Window
{
    public ApplySummaryDecision Decision {get;private set;} = new("Отмена");
    public ApplySummaryWindow(ApplySummary summary)
    {
        Style=(Style)FindResource(typeof(Window));Title="Применить перевод?";Width=660;MaxHeight=700;SizeToContent=SizeToContent.Height;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel {Margin=new Thickness(24)};Content=panel;
        panel.Children.Add(new TextBlock {Text=$"Будет применено: {summary.Selection.AppliedEntries:N0} строк\nПустых / непереведённых будет пропущено: {summary.Selection.SkippedEmptyTranslations:N0}\nФайлов будет изменено: {summary.Files:N0}\nСовместный перевод: включён\n\nЗакройте игру. Оригиналы сохраняются в резервной копии.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16)});
        var consent=new CheckBox {Content="Больше не спрашивать о совместном переводе",Margin=new Thickness(0,12,0,16),Visibility=summary.ShowCombinedWarning?Visibility.Visible:Visibility.Collapsed};
        if(summary.ShowCombinedWarning)panel.Children.Add(new TextBlock {Text="Русский текст записывается в существующий языковой слот игры. Отдельная поддержка русского не требуется.",TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(consent);
        var details=new Expander {Header="Подробнее",Content=new ScrollViewer {MaxHeight=220,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=new TextBlock {Text=summary.Description,TextWrapping=TextWrapping.Wrap}},Margin=new Thickness(0,0,0,12)};panel.Children.Add(details);
        var buttons=new WrapPanel();panel.Children.Add(buttons);
        foreach(var choice in new[]{"Применить","Показать пропущенные","Отмена"}) {
            var button=new Button {Content=choice,IsDefault=choice=="Применить",IsCancel=choice=="Отмена",IsEnabled=choice!="Показать пропущенные" || summary.Selection.SkippedEmptyTranslations>0};
            button.Click+=(_,_)=>{Decision=new(choice,consent.IsChecked==true);DialogResult=choice!="Отмена";};buttons.Children.Add(button);
        }
    }
}
