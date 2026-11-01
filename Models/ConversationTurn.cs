using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace QuickTranslate.Models;

public sealed class ConversationTurn : INotifyPropertyChanged
{
    public ConversationTurn(string question) => Question = question;

    public string Question { get; }
    public string Answer { get; private set; } = string.Empty;
    public string Error { get; private set; } = string.Empty;
    public bool IsPending { get; private set; } = true;
    public Visibility LoadingVisibility => IsPending ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => string.IsNullOrEmpty(Error) ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Start()
    {
        IsPending = true;
        Error = string.Empty;
        NotifyStateChanged();
    }

    public void Complete(string answer)
    {
        Answer = answer;
        IsPending = false;
        Error = string.Empty;
        NotifyStateChanged();
    }

    public void Fail(string error)
    {
        IsPending = false;
        Error = error;
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(Answer));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(LoadingVisibility));
        OnPropertyChanged(nameof(ErrorVisibility));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
