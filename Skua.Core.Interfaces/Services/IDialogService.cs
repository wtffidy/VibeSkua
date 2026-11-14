using Skua.Core.Interfaces.ViewModels;
using Skua.Core.Models;
using Skua.Core.Models.Servers;

namespace Skua.Core.Interfaces;

public interface IDialogService
{
    bool? ShowDialog<TViewModel>(TViewModel viewModel)
        where TViewModel : class;

    bool? ShowDialog<TViewModel>(TViewModel viewModel, string Title)
        where TViewModel : class;

    bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback)
        where TViewModel : class;

    void ShowDialog(IOptionContainer optionContainer, Action<IOptionContainerViewModel> callback);

    void ShowMessageBox(string message, string caption);

    bool? ShowMessageBox(string message, string caption, bool yesAndNo);

    DialogResult ShowMessageBox(string message, string caption, params string[] buttons);

    ServerLatencyWarningResult ShowServerLatencyWarning(string serverName, long pingMs, int thresholdMs);

    IInputDialogViewModel CreateInputDialog(string title, string dialogHint);
}